using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;
namespace IDVBuff.Features.Maps;
public sealed partial class MapStructureRegistrar
{
    private MapStructureRegistrationResult RegisterLegacy(
        MapStructureRegistrationRequest request)
    {
        var tuning = request.Tuning.Clone();
        tuning.Channel = request.Channel;
        tuning.Normalize();
        var vr = MapStructureValidator.ValidateRequest(request,
            usedRestrictedSearch: request.RestrictSearchToLockedTransform);
        if (vr is not null) return vr;
        var baselineScale = request.LockedTransform.ScaleX;
        var isLowStructureChannel =
            request.Channel == MapAlignmentChannel.LowStructure;
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "开始结构配准",
            details: new()
            {
                ["scaleSearchPolicy"] = request.ScaleSearchPolicy,
                ["trackingMode"] = request.TrackingMode,
                ["channel"] = request.Channel.ToString(),
                ["configFingerprint"] = isLowStructureChannel
                    ? tuning.CacheFingerprint
                    : "legacy"
            });
        var preprocessTimer = Stopwatch.StartNew();
        var preprocessSpan = MapOperationTraceAmbient.StartChild(
            "structure_preprocess",
            MapOperationWaitKind.Compute);
        using var ownedReference = request.PreparedReference is null
            ? _preprocessor.Process(
                request.ReferenceImage,
                generationTuning: tuning.Generation) : null;
        var reference = request.PreparedReference ?? ownedReference!;
        using var ownedLive = request.PreparedLive is null
            ? _preprocessor.ProcessLiveRoi(request.LiveRoi, request.LiveIgnoreRegions,
                request.DynamicIgnoreRegions,
                generateVisibleMask: tuning.EnableVisibleMask,
                generationTuning: tuning.Generation)
            : null;
        var live = request.PreparedLive ?? ownedLive!;
        preprocessTimer.Stop();
        preprocessSpan.Complete();
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "结构配准输入特征就绪",
            elapsedMs: preprocessTimer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["source"] = request.PreparedLive is null
                    ? "registrar-extraction"
                    : "caller-prepared",
                ["registrarExtractionMs"] =
                    preprocessTimer.Elapsed.TotalMilliseconds,
                ["originalFeatureExtractionMs"] =
                    live.DiagnosticTiming?.TotalMs ?? 0d
            });
        var debugDirectory = tuning.EnableDebugOutput
            ? MapStructureDebugOutput.ResolveDebugDirectory(request.DebugOutputDirectory) : null;
        MapStructureDebugOutput.WritePreprocessDebug(debugDirectory, request.LiveRoi, live, reference);
        var effectiveBaseline = baselineScale;
        Mat? dsEdges = null, dsStructure = null;
        Mat? ownedReferenceDistance = null;
        var isReciprocalScale = false;
        if (ShouldUseReciprocalScale(
                request.Channel,
                baselineScale,
                request.RestrictSearchToLockedTransform))
        {
            effectiveBaseline = 1.0; isReciprocalScale = true;
            var dsSize = new Size(
                Math.Max(1, (int)Math.Round(reference.Edges.Width * baselineScale)),
                Math.Max(1, (int)Math.Round(reference.Edges.Height * baselineScale)));
            dsEdges = new Mat();
            Cv2.Resize(reference.Edges, dsEdges, dsSize, 0d, 0d, InterpolationFlags.Area);
            Cv2.Threshold(dsEdges, dsEdges, 127d, 255d, ThresholdTypes.Binary);
            dsStructure = new Mat();
            Cv2.Resize(reference.StructureMask, dsStructure, dsSize, 0d, 0d, InterpolationFlags.Nearest);
            _currentReciprocalScale = new ReciprocalScaleContext
            { ReferenceScale = baselineScale, StructureMask = dsStructure };
        }
        try
        {
            var searchTimer = Stopwatch.StartNew();
            var distanceSpan = MapOperationTraceAmbient.StartChild(
                "distance_map",
                MapOperationWaitKind.Compute);
            var referenceDistance = dsEdges is null
                ? reference.GetOrCreateClippedReferenceDistanceMap(
                    tuning.DistanceClipPixels)
                : ownedReferenceDistance =
                    MapStructureScaleSearch.CreateDistanceMapFromEdges(
                        dsEdges, tuning.DistanceClipPixels);
            distanceSpan.Complete();
            var scaleSearchRadius = ResolveScaleSearchRadius(request, tuning);
            var hypotheses = BuildRegistrationScaleHypotheses(
                request, tuning, effectiveBaseline, baselineScale,
                scaleSearchRadius);
            using var ctx = new MapStructureScaleSearch.ScaleSearchContext
            {
                MarginNormalizationFloor = isLowStructureChannel
                    ? tuning.MarginNormalizationFloor
                    : StructureRegistrationRules.MarginNormalizationFloor
            };
            Mat? bestHeatmap = null;
            QueryGeometry? bestQuery = null;
            QueryGeometry? diagnosticQuery = null;
            var scaleSearchSpan = MapOperationTraceAmbient.StartChild(
                "structure_scale_search",
                MapOperationWaitKind.Compute);
            foreach (var (scale, hypothesisIndex) in hypotheses.Select(
                (scale, index) => (scale, index)))
            {
                if (tuning.EnforceTimeBudget
                    && searchTimer.ElapsedMilliseconds >= tuning.StructureFallbackBudgetMilliseconds)
                { ctx.TimeBudgetExceeded = true; break; }
                if (!isLowStructureChannel
                    && !tuning.DisableScaleEarlyTermination
                    && ctx.Candidates.Count > 0
                    && ctx.Candidates[0].CompositeCost
                        <= tuning.EarlyTerminationScoreThreshold)
                    break;
                var queryTimer = Stopwatch.StartNew();
                using var query = MapStructureScaleSearch.CreateQuery(
                    live, request.LiveRoi.Size(), scale,
                    includeVisibleMask: tuning.EnableVisibleAwareShadow
                        || tuning.EnableVisibleAwareInjection);
                queryTimer.Stop();
                ctx.QueryConstructionMs += queryTimer.Elapsed.TotalMilliseconds;
                diagnosticQuery ??= query.CloneForDebug();
                if (query.EdgeCount < tuning.MinimumEdgePixels
                    || query.Bounds.Width < tuning.MinimumSpanPixels
                    || query.Bounds.Height < tuning.MinimumSpanPixels)
                    continue;
                ctx.SufficientlyStructuredHypotheses++;
                var refEdgesForCheck = dsEdges ?? reference.Edges;
                if (!isLowStructureChannel
                    && (query.Bounds.Width >= refEdgesForCheck.Width
                        || query.Bounds.Height >= refEdgesForCheck.Height))
                { ctx.OversizedHypotheses++; continue; }
                var expected = MapStructureScaleSearch.ExpectedReferenceLocation(
                    request, scale, query.Bounds);
                var historyTimer = Stopwatch.StartNew();
                MapStructureCandidateCollector.CollectHistoryCandidates(
                    query, reference, referenceDistance, request, scale, tuning,
                    _currentReciprocalScale, ctx.Candidates);
                historyTimer.Stop();
                ctx.HistoryCandidateMs += historyTimer.Elapsed.TotalMilliseconds;
                if (request.RestrictSearchToLockedTransform)
                {
                    using var restrictedSearch = MapOperationTraceAmbient.StartChild(
                        "restricted_search",
                        MapOperationWaitKind.Compute,
                        attemptIndex: hypothesisIndex);
                    MapStructureScaleSearch.SearchRestrictedBranch(
                        query, reference, referenceDistance, live,
                        request, scale, expected, tuning, _currentReciprocalScale, ctx,
                        tuning.EnforceTimeBudget
                            ? Math.Max(0, tuning.StructureFallbackBudgetMilliseconds
                                - (int)searchTimer.ElapsedMilliseconds)
                            : int.MaxValue);
                }
                else if (isLowStructureChannel)
                    MapStructureScaleSearch.CollectFastCoarseCandidates(
                        query,
                        reference,
                        referenceDistance,
                        request,
                        scale,
                        tuning,
                        _currentReciprocalScale,
                        ctx.Candidates);
                else
                {
                    using var globalSearch = MapOperationTraceAmbient.StartChild(
                        "global_search",
                        MapOperationWaitKind.Compute,
                        attemptIndex: hypothesisIndex);
                    MapStructureScaleSearch.SearchGlobalBranch(
                        query, reference, referenceDistance, live,
                        request, scale, expected, tuning,
                        _currentReciprocalScale, isReciprocalScale, ctx);
                }
                if (tuning.EnforceTimeBudget
                    && searchTimer.ElapsedMilliseconds >= tuning.StructureFallbackBudgetMilliseconds)
                { ctx.TimeBudgetExceeded = true; break; }
                var scaleBest = ctx.Candidates
                    .Where(c => Math.Abs(c.Scale - scale) <
                        (isLowStructureChannel
                            ? tuning.ScaleDuplicateTolerance
                            : StructureRegistrationRules.ScaleDuplicateTolerance))
                    .OrderBy(c => c.CompositeCost).FirstOrDefault();
                if (scaleBest is not null
                    && (bestQuery is null
                        || scaleBest.CompositeCost < ctx.Candidates
                            .Where(c => Math.Abs(c.Scale - bestQuery.Scale)
                                < (isLowStructureChannel
                                    ? tuning.ScaleDuplicateTolerance
                                    : StructureRegistrationRules.ScaleDuplicateTolerance))
                            .Min(c => c.CompositeCost)))
                {
                    bestHeatmap?.Dispose(); bestHeatmap = null;
                    bestQuery?.Dispose(); bestQuery = query.CloneForDebug();
                }
            }
            searchTimer.Stop();
            scaleSearchSpan.Complete();
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                $"结构搜索完成 · {ctx.Candidates.Count} 个候选",
                elapsedMs: searchTimer.Elapsed.TotalMilliseconds,
                details: new()
                {
                    ["candidateCount"] = ctx.Candidates.Count,
                    ["hypotheses"] = hypotheses.Count,
                    ["baselineScale"] = effectiveBaseline,
                    ["scaleSearchRadius"] = scaleSearchRadius,
                    ["scaleSearchStep"] = tuning.ScaleSearchStep,
                    ["sufficientlyStructuredHypotheses"] = ctx.SufficientlyStructuredHypotheses,
                    ["oversizedHypotheses"] = ctx.OversizedHypotheses,
                    ["bestScore"] = ctx.Candidates.Count > 0
                        ? ctx.Candidates.Min(c => c.CompositeCost) : -1d,
                    ["bestScale"] = ctx.Candidates.Count > 0
                        ? ctx.Candidates.MinBy(c => c.CompositeCost)?.Scale : null,
                    ["usedFastStrategy"] = false,
                    ["usedRestrictedSearch"] = request.RestrictSearchToLockedTransform,
                    ["timeBudgetExceeded"] = ctx.TimeBudgetExceeded,
                    ["workPreflightRejected"] = ctx.WorkPreflightRejected,
                    ["estimatedRestrictedTemplateMs"] = ctx.EstimatedRestrictedTemplateMilliseconds,
                    ["queryConstructionMs"] = ctx.QueryConstructionMs,
                    ["historyCandidateMs"] = ctx.HistoryCandidateMs,
                    ["visibleAwareSearchMs"] = ctx.VisibleAwareTotalMs,
                    ["visibleAwareRequestedBackend"] = ctx.VisibleAwareSession?.RequestedBackend,
                    ["visibleAwareActualBackend"] = ctx.VisibleAwareSession?.ActualBackend,
                    ["visibleAwareUMatFallbackReason"] = ctx.VisibleAwareSession?.FallbackReason,
                    ["visibleAwareCoarseMs"] = ctx.VisibleAwareCoarseMs,
                    ["visibleAwareRefineMs"] = ctx.VisibleAwareRefineMs,
                    ["visibleAwareUploadMs"] = ctx.VisibleAwareSession?.UploadMilliseconds ?? 0d,
                    ["visibleAwareDownloadMs"] = ctx.VisibleAwareSession?.DownloadMilliseconds ?? 0d,
                    ["visibleAwareCompletedScales"] = ctx.VisibleAwareCompletedScales,
                    ["visibleAwareBudgetSkippedScales"] = ctx.VisibleAwareBudgetSkippedScales,
                    ["visibleAwareCoarsePeaks"] = ctx.VisibleAwareCoarsePeaks,
                    ["visibleAwareRefinedCandidates"] = ctx.VisibleAwareRefinedCandidates,
                    ["featureVotingMs"] = ctx.FeatureVotingMs,
                    ["pyramidSearchMs"] = ctx.PyramidSearchMs,
                    ["localTemplateSearchMs"] = ctx.LocalTemplateSearchMs,
                    ["globalTemplateSearchMs"] = ctx.GlobalTemplateSearchMs,
                    ["referenceWidth"] = reference.Edges.Width,
                    ["referenceHeight"] = reference.Edges.Height,
                    ["queryEdgePixels"] = diagnosticQuery?.EdgeCount ?? 0,
                    ["queryDiagnosticScale"] = diagnosticQuery?.Scale,
                    ["queryBoundsX"] = diagnosticQuery?.Bounds.X ?? 0,
                    ["queryBoundsY"] = diagnosticQuery?.Bounds.Y ?? 0,
                    ["queryBoundsWidth"] =
                        diagnosticQuery?.Bounds.Width ?? 0,
                    ["queryBoundsHeight"] =
                        diagnosticQuery?.Bounds.Height ?? 0,
                    ["visibleAwareEarlyAccepted"] = ctx.VisibleAwareEarlyAccepted,
                    ["visibleAwareFallbackReason"] = ctx.VisibleAwareFallbackReason
                });
            try
            {
                var rankingSpan = MapOperationTraceAmbient.StartChild(
                    "candidate_ranking",
                    MapOperationWaitKind.Compute);
                var rankingTimer = Stopwatch.StartNew();
                var ranking =
                    MapStructureCandidateCollector.RankCandidatesByValidity(
                        ctx.Candidates,
                        tuning,
                        request.LockedTransform,
                        request.RestrictSearchToLockedTransform);
                var allRanked = ranking.Ordered;
                var diagnosticRanked = ranking.Diagnostic;
                var rawBest = allRanked.FirstOrDefault();
                var rawBestRejection = rawBest is null
                    ? MapStructureRejectionReason.NoCandidate
                    : MapStructureValidator.ValidateAbsolute(
                        rawBest, tuning, request.RestrictSearchToLockedTransform);
                var ranked = ranking.Valid;
                using var rankedQuery = ranked.Length > 0
                    ? MapStructureScaleSearch.CreateQuery(
                        live,
                        request.LiveRoi.Size(),
                        ranked[0].Scale
                            / _currentReciprocalScale.ReferenceScale)
                    : null;
                MapStructureDebugOutput.WriteSearchDebug(
                    debugDirectory, reference, bestHeatmap, bestQuery, ranked);
                rankingTimer.Stop();
                rankingSpan.Complete();
                var d = new MapStructureValidator.LegacyDiagnostics(
                    ctx,
                    PreprocessMs: preprocessTimer.Elapsed.TotalMilliseconds,
                    SearchMs: searchTimer.Elapsed.TotalMilliseconds,
                    CandidateRankingMs: rankingTimer.Elapsed.TotalMilliseconds,
                    DebugDirectory: debugDirectory,
                    LockedScale: baselineScale,
                    ReferenceWidth: reference.Edges.Width,
                    ReferenceHeight: reference.Edges.Height,
                    QueryEdgePixels: rankedQuery?.EdgeCount
                        ?? diagnosticQuery?.EdgeCount
                        ?? 0,
                    QueryBounds: rankedQuery?.Bounds ?? diagnosticQuery?.Bounds,
                    ScaleHypothesisCount: hypotheses.Count,
                    OversizedHypothesisCount: ctx.OversizedHypotheses,
                    UsedRestrictedSearch: request.RestrictSearchToLockedTransform,
                    VisibleMaskMs: live.DiagnosticTiming?.VisibleMaskMs ?? 0d);
                if (ranked.Length == 0)
                {
                    var reason = allRanked.Length > 0
                        ? rawBestRejection
                        : ctx.TimeBudgetExceeded
                        ? MapStructureRejectionReason.TimeBudgetExceeded
                        : ctx.SufficientlyStructuredHypotheses == 0
                            ? MapStructureRejectionReason.InsufficientStructure
                            : ctx.OversizedHypotheses == ctx.SufficientlyStructuredHypotheses
                                ? MapStructureRejectionReason.QueryLargerThanReference
                                : MapStructureRejectionReason.NoCandidate;
                    MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                        $"结构配准未通过：{reason.ToDisplayText()}");
                    return MapStructureValidator.BuildLegacyResult(
                        reason, d, candidates: diagnosticRanked);
                }
                if (rawBest is not null && !ReferenceEquals(rawBest, ranked[0]))
                {
                    MapLogCollector.Instance.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Info,
                        "原始最低成本候选未通过绝对质量门，继续采用后续有效候选",
                        details: new()
                        {
                            ["rawBestScore"] = rawBest.CompositeCost,
                            ["rawBestRejection"] = rawBestRejection.ToString(),
                            ["selectedScore"] = ranked[0].CompositeCost
                        });
                }
                var refineTimer = Stopwatch.StartNew();
                var forcedRefinementFallback = false;
                MapStructureRefiner.EccRefinementDiagnostics? eccDiagnostics = null;
                var refined = ranked[0];
                if (!MapStructureRefiner.CanSkipLocalRefinement(
                        ranked,
                        tuning,
                        request.RestrictSearchToLockedTransform))
                {
                    using var refineSpan = MapOperationTraceAmbient.StartChild(
                        "structure_refinement",
                        MapOperationWaitKind.Compute);
                    refined = MapStructureRefiner.RefineCandidate(
                        ranked[0], live, reference,
                        referenceDistance, request, tuning, _currentReciprocalScale,
                        tuning.EnforceTimeBudget
                            ? Math.Max(0, tuning.StructureFallbackBudgetMilliseconds
                                - (int)Math.Ceiling(searchTimer.Elapsed.TotalMilliseconds))
                            : int.MaxValue,
                        out eccDiagnostics);
                }
                refineTimer.Stop();
                LogEccRefinement(refined, eccDiagnostics,
                    refineTimer.Elapsed.TotalMilliseconds);
                var refinementWorsenTolerance = isLowStructureChannel
                    ? tuning.RefinementWorsenTolerance
                    : StructureRegistrationRules.RefinementWorsenTolerance;
                if (refined.CompositeCost > ranked[0].CompositeCost
                    + refinementWorsenTolerance)
                {
                    if (!request.ForceBestCandidate)
                    {
                        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                            $"结构配准未通过：{MapStructureRejectionReason.RefinementFailed.ToDisplayText()}");
                        return MapStructureValidator.BuildLegacyResult(
                            MapStructureRejectionReason.RefinementFailed, d, candidates: ranked);
                    }
                    refined = ranked[0];
                    forcedRefinementFallback = true;
                }
                var validFinalRanked = new[] { refined }.Concat(ranked.Skip(1))
                    .OrderBy(c => c.CompositeCost).ToArray();
                var finalRanked = validFinalRanked.Concat(
                        allRanked.Where(candidate => !ranked.Contains(candidate)))
                    .Take(tuning.TopCandidateCount)
                    .ToArray();
                var best = finalRanked[0];
                var secondScore = validFinalRanked.Length > 1
                    ? validFinalRanked[1].CompositeCost : double.PositiveInfinity;
                var margin = double.IsPositiveInfinity(secondScore) ? 1d
                    : Math.Clamp((secondScore - best.CompositeCost)
                        / Math.Max(
                            isLowStructureChannel
                                ? tuning.MarginNormalizationFloor
                                : StructureRegistrationRules.MarginNormalizationFloor,
                            secondScore), 0d, 1d);
                var requiredMargin = tuning.MinimumCandidateMargin
                    * (best.UsedGlobalSearch
                        ? isLowStructureChannel
                            ? tuning.GlobalSearchMarginMultiplier
                            : StructureRegistrationRules.GlobalSearchMarginMultiplier
                        : 1d);
                var rejection = MapStructureValidator.Validate(
                    best, margin, requiredMargin, tuning,
                    restrictedSearch: request.RestrictSearchToLockedTransform);
                var allowedScaleChange = Math.Max(
                    isLowStructureChannel
                        ? tuning.MaximumScaleChangeRatio
                        : StructureRegistrationRules.MaximumScaleChangeRatio,
                    scaleSearchRadius);
                if (rejection == MapStructureRejectionReason.None
                    && !request.ForceBestCandidate
                    && !(isLowStructureChannel
                        && request.ScaleSearchPolicy == MapScaleSearchPolicy.Search)
                    && double.IsFinite(request.LockedTransform.ScaleX)
                    && request.LockedTransform.ScaleX > 0d
                    && Math.Abs((best.Scale / request.LockedTransform.ScaleX) - 1d)
                        > allowedScaleChange)
                {
                    rejection = MapStructureRejectionReason.ScaleChangeTooLarge;
                }
                var forcedReason = forcedRefinementFallback
                    ? MapStructureRejectionReason.RefinementFailed : rejection;
                var confidenceBreakdown = MapStructureConfidenceCalculator.Calculate(
                    best, margin, tuning, rejection,
                    isTrackingMode: request.TrackingMode,
                    sideEntrancePrior: request.SideEntrancePrior);
                var confidence = confidenceBreakdown.FinalScore;
                var dd = d with { RefineMs = refineTimer.Elapsed.TotalMilliseconds };
                if (rejection != MapStructureRejectionReason.None && !request.ForceBestCandidate)
                {
                    var rd = CreateConfidenceLogDetails(confidenceBreakdown);
                    rd["bestScore"] = best.CompositeCost; rd["margin"] = margin;
                    MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                        $"结构配准未通过：{rejection.ToDisplayText()}", details: rd);
                    return MapStructureValidator.BuildLegacyResult(rejection, dd,
                        candidates: finalRanked,
                        confidence: confidence, confidenceBreakdown: confidenceBreakdown,
                        bestScore: best.CompositeCost, secondScore: secondScore,
                        candidateMargin: margin,
                        featureConsensus: best.FeatureConsensus,
                        eccConverged: best.EccConverged, eccCorrelation: best.EccCorrelation);
                }
                var transform = MapStructureValidator.BuildTransform(best, request, reference);
                MapStructureDebugOutput.WriteFinalDebug(debugDirectory, request, reference, live, transform);
                var ad = CreateConfidenceLogDetails(confidenceBreakdown);
                ad["bestScore"] = best.CompositeCost; ad["margin"] = margin;
                ad["usedFastStrategy"] = false;
                ad["scaleHypotheses"] = hypotheses.Count;
                ad["visibleAwareEarlyAccepted"] = ctx.VisibleAwareEarlyAccepted;
                ad["visibleAwareFallbackReason"] = ctx.VisibleAwareFallbackReason;
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    $"结构配准通过 · 置信度 {confidence:P0} · 最佳分数 {best.CompositeCost:F3}",
                    details: ad);
                return MapStructureValidator.BuildLegacyResult(forcedReason, dd,
                    accepted: true, transform: transform,
                    candidates: finalRanked,
                    confidence: confidence, confidenceBreakdown: confidenceBreakdown,
                    bestScore: best.CompositeCost, secondScore: secondScore,
                    candidateMargin: margin,
                    wasForcedBestCandidate: forcedReason != MapStructureRejectionReason.None,
                    featureConsensus: best.FeatureConsensus,
                    eccConverged: best.EccConverged, eccCorrelation: best.EccCorrelation);
            }
            finally
            {
                bestHeatmap?.Dispose();
                bestQuery?.Dispose();
                diagnosticQuery?.Dispose();
            }
        }
        finally
        {
            ownedReferenceDistance?.Dispose();
            dsEdges?.Dispose();
            dsStructure?.Dispose();
            _currentReciprocalScale = ReciprocalScaleContext.None;
        }
    }
    private MapStructureRegistrationResult TryFastCoarseAlign(
        MapStructureRegistrationRequest request)
    {
        var tuning = request.Tuning.Clone();
        tuning.Channel = request.Channel;
        tuning.Normalize();
        var vr = MapStructureValidator.ValidateRequest(
            request,
            usedRestrictedSearch: request.RestrictSearchToLockedTransform);
        if (vr is not null) return vr;
        var baselineScale = request.LockedTransform.ScaleX;
        var preprocessTimerFast = Stopwatch.StartNew();
        using var ownedReferenceFast = request.PreparedReference is null
            ? _preprocessor.Process(
                request.ReferenceImage,
                generationTuning: tuning.Generation) : null;
        var referenceFast = request.PreparedReference ?? ownedReferenceFast!;
        using var ownedLiveFast = request.PreparedLive is null
            ? _preprocessor.ProcessLiveRoi(request.LiveRoi, request.LiveIgnoreRegions,
                request.DynamicIgnoreRegions,
                generateVisibleMask: tuning.EnableVisibleMask,
                generationTuning: tuning.Generation)
            : null;
        var liveFast = request.PreparedLive ?? ownedLiveFast!;
        preprocessTimerFast.Stop();
        var effectiveBaseline = baselineScale;
        Mat? dsEdgesFast = null, dsStructureFast = null;
        Mat? ownedReferenceDistanceFast = null;
        if (ShouldUseReciprocalScale(
                request.Channel,
                baselineScale,
                request.RestrictSearchToLockedTransform))
        {
            effectiveBaseline = 1.0;
            var dsSize = new Size(
                Math.Max(1, (int)Math.Round(referenceFast.Edges.Width * baselineScale)),
                Math.Max(1, (int)Math.Round(referenceFast.Edges.Height * baselineScale)));
            dsEdgesFast = new Mat();
            Cv2.Resize(referenceFast.Edges, dsEdgesFast, dsSize, 0d, 0d, InterpolationFlags.Area);
            Cv2.Threshold(dsEdgesFast, dsEdgesFast, 127d, 255d, ThresholdTypes.Binary);
            dsStructureFast = new Mat();
            Cv2.Resize(referenceFast.StructureMask, dsStructureFast, dsSize, 0d, 0d, InterpolationFlags.Nearest);
            _currentReciprocalScale = new ReciprocalScaleContext
            { ReferenceScale = baselineScale, StructureMask = dsStructureFast };
        }
        try
        {
            var referenceDistance = dsEdgesFast is null
                ? referenceFast.GetOrCreateClippedReferenceDistanceMap(
                    tuning.DistanceClipPixels)
                : ownedReferenceDistanceFast =
                    MapStructureScaleSearch.CreateDistanceMapFromEdges(
                        dsEdgesFast, tuning.DistanceClipPixels);
            var preprocessMs = preprocessTimerFast.Elapsed.TotalMilliseconds;
            var coarseTimer = Stopwatch.StartNew();
            var candidates = new List<MapStructureCandidate>();
            using var query = MapStructureScaleSearch.CreateQuery(
                liveFast, request.LiveRoi.Size(), effectiveBaseline,
                includeVisibleMask: tuning.EnableVisibleAwareShadow
                    || tuning.EnableVisibleAwareInjection);
            var refEdgesForCheck = dsEdgesFast ?? referenceFast.Edges;
            if (query.EdgeCount < tuning.MinimumEdgePixels
                || query.Bounds.Width < tuning.MinimumSpanPixels
                || query.Bounds.Height < tuning.MinimumSpanPixels)
            {
                return MapStructureValidator.BuildResult(
                    MapStructureRejectionReason.InsufficientStructure,
                    preprocessMs: preprocessMs,
                    searchMs: coarseTimer.Elapsed.TotalMilliseconds,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds);
            }
            if (query.Bounds.Width >= refEdgesForCheck.Width
                || query.Bounds.Height >= refEdgesForCheck.Height)
            {
                return MapStructureValidator.BuildResult(
                    MapStructureRejectionReason.QueryLargerThanReference,
                    preprocessMs: preprocessMs,
                    searchMs: coarseTimer.Elapsed.TotalMilliseconds,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds);
            }
            using var visibleContext =
                new MapStructureScaleSearch.ScaleSearchContext
                {
                    MarginNormalizationFloor = request.Channel ==
                        MapAlignmentChannel.LowStructure
                            ? tuning.MarginNormalizationFloor
                            : StructureRegistrationRules.MarginNormalizationFloor
                };
            var visibleAware =
                MapStructureVisibleAwareSearch.CollectVisibleAwareCandidates(
                    query,
                    referenceFast,
                    referenceDistance,
                    request,
                    effectiveBaseline,
                    tuning,
                    _currentReciprocalScale,
                    visibleContext,
                    candidates);
            if (!visibleAware.Ran || candidates.Count == 0)
            {
                MapStructureScaleSearch.CollectFastCoarseCandidates(
                    query, referenceFast, referenceDistance,
                    request, effectiveBaseline, tuning,
                    _currentReciprocalScale, candidates);
            }
            MapStructureCandidateCollector.CollectHistoryCandidates(
                query, referenceFast, referenceDistance,
                request, effectiveBaseline, tuning, _currentReciprocalScale, candidates);
            coarseTimer.Stop();
            var coarseMs = coarseTimer.Elapsed.TotalMilliseconds;
            var ranking = MapStructureCandidateCollector.RankCandidatesByValidity(
                candidates,
                tuning,
                request.LockedTransform,
                request.RestrictSearchToLockedTransform);
            var allRanked = ranking.Ordered;
            var diagnosticRanked = ranking.Diagnostic;
            var rawBest = allRanked.FirstOrDefault();
            var rawBestRejection = rawBest is null
                ? MapStructureRejectionReason.NoCandidate
                : MapStructureValidator.ValidateAbsolute(
                    rawBest, tuning, request.RestrictSearchToLockedTransform);
            var ranked = ranking.Valid;
            if (ranked.Length == 0)
            {
                return MapStructureValidator.BuildResult(
                    allRanked.Length > 0
                        ? rawBestRejection
                        : MapStructureRejectionReason.NoCandidate,
                    candidates: diagnosticRanked,
                    preprocessMs: preprocessMs,
                    searchMs: coarseMs,
                    usedFastStrategy: true,
                    scaleHypothesisCount: 1,
                    usedRestrictedSearch: request.RestrictSearchToLockedTransform,
                    fastCoarseSearchMs: coarseMs, fastCoarseCandidateCount: candidates.Count,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds);
            }
            var refineTimer = Stopwatch.StartNew();
            var refined = MapStructureRefiner.RefineCandidate(ranked[0],
                liveFast, referenceFast, referenceDistance, request, tuning,
                _currentReciprocalScale,
                tuning.EnforceTimeBudget
                    ? Math.Max(0, tuning.StructureFallbackBudgetMilliseconds
                        - (int)Math.Ceiling(coarseMs))
                    : int.MaxValue, out var eccDiagnostics);
            refineTimer.Stop();
            LogEccRefinement(refined, eccDiagnostics,
                refineTimer.Elapsed.TotalMilliseconds);
            var fastRefinementWorsenTolerance = request.Channel ==
                MapAlignmentChannel.LowStructure
                    ? tuning.RefinementWorsenTolerance
                    : StructureRegistrationRules.RefinementWorsenTolerance;
            if (refined.CompositeCost > ranked[0].CompositeCost
                    + fastRefinementWorsenTolerance
                && !request.ForceBestCandidate)
            {
                return MapStructureValidator.BuildResult(
                    MapStructureRejectionReason.RefinementFailed, candidates: ranked,
                    preprocessMs: preprocessMs,
                    searchMs: coarseMs, refineMs: refineTimer.Elapsed.TotalMilliseconds,
                    usedFastStrategy: true,
                    scaleHypothesisCount: 1,
                    usedRestrictedSearch: request.RestrictSearchToLockedTransform,
                    fastCoarseSearchMs: coarseMs, fastCoarseCandidateCount: candidates.Count,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds);
            }
            var validFinalRanked = new[] { refined }.Concat(ranked.Skip(1))
                .OrderBy(c => c.CompositeCost).ToArray();
            var finalRanked = validFinalRanked.Concat(
                    allRanked.Where(candidate => !ranked.Contains(candidate)))
                .Take(tuning.TopCandidateCount)
                .ToArray();
            var best = finalRanked[0];
            var secondScore = validFinalRanked.Length > 1
                ? validFinalRanked[1].CompositeCost : double.PositiveInfinity;
            var margin = double.IsPositiveInfinity(secondScore) ? 1d
                : Math.Clamp((secondScore - best.CompositeCost)
                    / Math.Max(
                        request.Channel == MapAlignmentChannel.LowStructure
                            ? tuning.MarginNormalizationFloor
                            : StructureRegistrationRules.MarginNormalizationFloor,
                        secondScore), 0d, 1d);
            var requiredMargin = tuning.MinimumCandidateMargin
                * (best.UsedGlobalSearch
                    ? request.Channel == MapAlignmentChannel.LowStructure
                        ? tuning.GlobalSearchMarginMultiplier
                        : StructureRegistrationRules.GlobalSearchMarginMultiplier
                    : 1d);
            var rejection = MapStructureValidator.Validate(
                best, margin, requiredMargin, tuning,
                restrictedSearch: request.RestrictSearchToLockedTransform);
            var confidenceBreakdown = MapStructureConfidenceCalculator.Calculate(
                best, margin, tuning, rejection,
                isTrackingMode: request.TrackingMode,
                sideEntrancePrior: request.SideEntrancePrior);
            if (rejection == MapStructureRejectionReason.None)
            {
                rejection = MapStructureValidator.ValidateFastConfidence(
                    confidenceBreakdown,
                    StructureRegistrationRules.FastMinimumGeometricLockConfidence);
                if (rejection != MapStructureRejectionReason.None)
                {
                    confidenceBreakdown = MapStructureConfidenceCalculator.Calculate(
                        best, margin, tuning, rejection,
                        isTrackingMode: request.TrackingMode,
                        sideEntrancePrior: request.SideEntrancePrior);
                }
            }
            var confidence = confidenceBreakdown.FinalScore;
            if (rejection != MapStructureRejectionReason.None && !request.ForceBestCandidate)
            {
                var rd = CreateConfidenceLogDetails(confidenceBreakdown);
                rd["fastCoarseMs"] = coarseMs; rd["fastCandidates"] = candidates.Count;
                rd["bestScore"] = best.CompositeCost; rd["rejection"] = rejection.ToString();
                rd["referenceWidth"] = refEdgesForCheck.Width;
                rd["referenceHeight"] = refEdgesForCheck.Height;
                rd["queryEdgePixels"] = query.EdgeCount;
                rd["queryBoundsX"] = query.Bounds.X;
                rd["queryBoundsY"] = query.Bounds.Y;
                rd["queryBoundsWidth"] = query.Bounds.Width;
                rd["queryBoundsHeight"] = query.Bounds.Height;
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                    $"快速粗搜索未通过验证：{rejection.ToDisplayText()}",
                    elapsedMs: coarseMs + refineTimer.Elapsed.TotalMilliseconds, details: rd);
                return MapStructureValidator.BuildResult(rejection,
                    candidates: finalRanked,
                    preprocessMs: preprocessMs,
                    searchMs: coarseMs, refineMs: refineTimer.Elapsed.TotalMilliseconds,
                    usedFastStrategy: true,
                    scaleHypothesisCount: 1,
                    usedRestrictedSearch: request.RestrictSearchToLockedTransform,
                    fastCoarseSearchMs: coarseMs, fastCoarseCandidateCount: candidates.Count,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds,
                    confidence: confidence, confidenceBreakdown: confidenceBreakdown,
                    bestScore: best.CompositeCost, secondScore: secondScore,
                    candidateMargin: margin,
                    eccConverged: best.EccConverged, eccCorrelation: best.EccCorrelation);
            }
            var transform = MapStructureValidator.BuildTransform(best, request, referenceFast);
            var ad = CreateConfidenceLogDetails(confidenceBreakdown);
            ad["bestScore"] = best.CompositeCost; ad["margin"] = margin;
            ad["fastCoarseMs"] = coarseMs; ad["fastCandidates"] = candidates.Count;
            ad["referenceWidth"] = refEdgesForCheck.Width;
            ad["referenceHeight"] = refEdgesForCheck.Height;
            ad["queryEdgePixels"] = query.EdgeCount;
            ad["queryBoundsX"] = query.Bounds.X;
            ad["queryBoundsY"] = query.Bounds.Y;
            ad["queryBoundsWidth"] = query.Bounds.Width;
            ad["queryBoundsHeight"] = query.Bounds.Height;
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                $"快速粗搜索通过 · 置信度 {confidence:P0} · 最佳分数 {best.CompositeCost:F3} · 粗搜索 {coarseMs:F1}ms",
                details: ad);
            return MapStructureValidator.BuildResult(MapStructureRejectionReason.None,
                accepted: true, transform: transform, candidates: finalRanked,
                preprocessMs: preprocessMs,
                searchMs: coarseMs, refineMs: refineTimer.Elapsed.TotalMilliseconds,
                usedFastStrategy: true,
                scaleHypothesisCount: 1,
                usedRestrictedSearch: request.RestrictSearchToLockedTransform,
                fastCoarseSearchMs: coarseMs, fastCoarseCandidateCount: candidates.Count,
                lockedScale: baselineScale,
                referenceWidth: refEdgesForCheck.Width,
                referenceHeight: refEdgesForCheck.Height,
                queryEdgePixels: query.EdgeCount,
                queryBounds: query.Bounds,
                confidence: confidence, confidenceBreakdown: confidenceBreakdown,
                bestScore: best.CompositeCost, secondScore: secondScore,
                candidateMargin: margin,
                eccConverged: best.EccConverged, eccCorrelation: best.EccCorrelation);
        }
        finally
        {
            ownedReferenceDistanceFast?.Dispose();
            dsEdgesFast?.Dispose();
            dsStructureFast?.Dispose();
            _currentReciprocalScale = ReciprocalScaleContext.None;
        }
    }
}
