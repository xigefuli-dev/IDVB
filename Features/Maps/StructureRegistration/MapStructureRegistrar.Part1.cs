using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;
namespace IDVBuff.Features.Maps;public sealed partial class MapStructureRegistrar
{
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
                candidates, tuning,
                request.LockedTransform,
                request.RestrictSearchToLockedTransform, request);
            var allRanked = ranking.Ordered;
            var diagnosticRanked = ranking.Diagnostic;
            var rawBest = allRanked.FirstOrDefault();
            var rawBestRejection = rawBest is null
                ? MapStructureRejectionReason.NoCandidate
                : MapStructureValidator.ValidateAbsolute(
                    rawBest, tuning,
                    request.RestrictSearchToLockedTransform, request);
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
            var validFinalRanked = new[] { refined }.Concat(ranked.Skip(1)).OrderBy(c => c.CompositeCost).ToArray();
            var finalRanked = validFinalRanked.Concat(allRanked.Where(candidate => !ranked.Contains(candidate))).Take(tuning.TopCandidateCount).ToArray();
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
                request.RestrictSearchToLockedTransform, request);
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
