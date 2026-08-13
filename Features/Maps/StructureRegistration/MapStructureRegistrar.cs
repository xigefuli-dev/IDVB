using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Registers positive live map structure against one already-selected full
/// reference map. The only permitted transform is uniform scale + translation.
/// </summary>
public sealed class MapStructureRegistrar
{
    private readonly MapStructurePreprocessor _preprocessor;

    // RegisterLegacy/TryFastCoarseAlign use _currentReciprocalScale to carry a
    // temporary Mat through the nested scoring helpers.  The registrar is a
    // shared service, so concurrent map-open alignments could overwrite that
    // context while another call was still using it.  The overwritten context
    // then pointed at a Mat already disposed by the other call.  Registration
    // is CPU/OpenCV work and is synchronous; serialize only this critical
    // section so the temporary context cannot cross request boundaries.
    private readonly object _registrationGate = new();

    /// <summary>互逆参考图缩放上下文：当 baselineScale &lt; 1.0 时，
    /// 降采样参考图而非升采样 query，保持边缘锐利。
    /// 由 RegisterLegacy / TryFastCoarseAlign 设置，Evaluate 读取。</summary>
    private ReciprocalScaleContext _currentReciprocalScale = ReciprocalScaleContext.None;

    internal sealed class ReciprocalScaleContext
    {
        public double ReferenceScale { get; init; } = 1.0;
        public Mat? StructureMask { get; init; }
        public static readonly ReciprocalScaleContext None = new();
    }

    public MapStructureRegistrar(MapStructurePreprocessor preprocessor)
    {
        _preprocessor = preprocessor;
    }

    public MapStructureRegistrationResult Register(
        MapStructureRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_registrationGate)
        {
            var tuning = request.Tuning.Clone();
            tuning.Normalize();

            var savedReciprocalScale = _currentReciprocalScale;
            _currentReciprocalScale = ReciprocalScaleContext.None;
            try { return RegisterInternal(request, tuning); }
            finally { _currentReciprocalScale = savedReciprocalScale; }
        }
    }

    private MapStructureRegistrationResult RegisterInternal(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning)
    {
        const bool canUseFast = true;

        if (tuning.FastAlignmentShadowMode && canUseFast)
        {
            var legacyResult = RegisterLegacy(request);
            try
            {
                var shadowFast = TryFastCoarseAlign(request);
                var ft = shadowFast.Transform;
                var lt = legacyResult.Transform;
                var td = 0d; var sd = 0d;
                if (ft is not null && lt is not null)
                {
                    td = Math.Sqrt(Math.Pow(ft.OffsetX - lt.OffsetX, 2d)
                        + Math.Pow(ft.OffsetY - lt.OffsetY, 2d));
                    sd = Math.Abs(ft.ScaleX - lt.ScaleX);
                }
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"Shadow对比 · Fast={(shadowFast.Accepted ? "通过" : "未通过")} "
                    + $"Legacy={(legacyResult.Accepted ? "通过" : "未通过")} "
                    + $"Δ={td:F1}px Δs={sd:F4}",
                    details: new()
                    {
                        ["fastAccepted"] = shadowFast.Accepted,
                        ["legacyAccepted"] = legacyResult.Accepted,
                        ["transformDeltaPx"] = td, ["scaleDelta"] = sd,
                        ["fastTotalMs"] = shadowFast.SearchMilliseconds + shadowFast.RefineMilliseconds,
                        ["legacyTotalMs"] = legacyResult.SearchMilliseconds + legacyResult.RefineMilliseconds,
                        ["fastRejection"] = shadowFast.RejectionReason.ToString(),
                        ["legacyRejection"] = legacyResult.RejectionReason.ToString(),
                    });
            }
            catch { }
            return legacyResult;
        }

        if (tuning.EnableFastAlignment && canUseFast)
        {
            try
            {
                var fastResult = TryFastCoarseAlign(request);
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    fastResult.Accepted ? MapLogLevel.Info : MapLogLevel.Warning,
                    fastResult.Accepted
                        ? "快速粗搜索通过"
                        : tuning.FastFallbackToLegacy
                            ? "快速粗搜索未早停，将进入完整搜索"
                            : "快速粗搜索未通过",
                    elapsedMs: fastResult.PreprocessMilliseconds
                        + fastResult.SearchMilliseconds
                        + fastResult.RefineMilliseconds,
                    details: new()
                    {
                        ["usedFastStrategy"] = true, ["accepted"] = fastResult.Accepted,
                        ["fallbackToLegacy"] =
                            !fastResult.Accepted && tuning.FastFallbackToLegacy,
                        ["preprocessMs"] = fastResult.PreprocessMilliseconds,
                        ["fastCoarseMs"] = fastResult.FastCoarseSearchMilliseconds,
                        ["fastCandidates"] = fastResult.FastCoarseCandidateCount,
                        ["rejection"] = fastResult.RejectionReason.ToString(),
                        ["lockedScale"] = fastResult.LockedScale,
                        ["referenceWidth"] = fastResult.ReferenceWidth,
                        ["referenceHeight"] = fastResult.ReferenceHeight,
                        ["queryEdgePixels"] = fastResult.QueryEdgePixels,
                        ["queryBoundsX"] = fastResult.QueryBoundsX,
                        ["queryBoundsY"] = fastResult.QueryBoundsY,
                        ["queryBoundsWidth"] = fastResult.QueryBoundsWidth,
                        ["queryBoundsHeight"] = fastResult.QueryBoundsHeight
                    });
                if (fastResult.Accepted) return fastResult;
                if (!tuning.FastFallbackToLegacy) return fastResult;
            }
            catch (Exception ex)
            {
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                    MapLogLevel.Error, $"快速粗搜索异常，回退 Legacy：{ex.Message}");
            }
        }

        return RegisterLegacy(request);
    }

    // ═══════════════════════════════════════════════════════════════
    // Legacy 全搜索路径
    // ═══════════════════════════════════════════════════════════════

    private MapStructureRegistrationResult RegisterLegacy(
        MapStructureRegistrationRequest request)
    {
        var tuning = request.Tuning.Clone();
        tuning.Normalize();

        var vr = MapStructureValidator.ValidateRequest(request,
            usedRestrictedSearch: request.RestrictSearchToLockedTransform);
        if (vr is not null) return vr;

        var baselineScale = request.LockedTransform.ScaleX;
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "开始结构配准",
            details: new() { ["scaleSearchPolicy"] = request.ScaleSearchPolicy, ["trackingMode"] = request.TrackingMode });

        // ── 预处理 ──
        var preprocessTimer = Stopwatch.StartNew();
        using var ownedReference = request.PreparedReference is null
            ? _preprocessor.Process(request.ReferenceImage) : null;
        var reference = request.PreparedReference ?? ownedReference!;
        using var ownedLive = request.PreparedLive is null
            ? _preprocessor.ProcessLiveRoi(request.LiveRoi, request.LiveIgnoreRegions,
                request.DynamicIgnoreRegions, generateVisibleMask: tuning.EnableVisibleMask)
            : null;
        var live = request.PreparedLive ?? ownedLive!;
        preprocessTimer.Stop();
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

        // ── 互逆参考图缩放 ──
        var effectiveBaseline = baselineScale;
        Mat? dsEdges = null, dsStructure = null;
        var isReciprocalScale = false;
        if (baselineScale < 1.0 && !request.RestrictSearchToLockedTransform)
        {
            effectiveBaseline = 1.0; isReciprocalScale = true;
            var dsSize = new Size(
                Math.Max(1, (int)Math.Round(reference.Edges.Width * baselineScale)),
                Math.Max(1, (int)Math.Round(reference.Edges.Height * baselineScale)));
            dsEdges = new Mat();
            Cv2.Resize(reference.Edges, dsEdges, dsSize, 0d, 0d, InterpolationFlags.Area);
            // Area 平均会把 1px Canny 二值边降采样成灰值，随后 DistanceTransform
            // 把灰边当非前景、从参考距离图抹掉墙段（根因④）。重阈值化保持二值。
            Cv2.Threshold(dsEdges, dsEdges, 127d, 255d, ThresholdTypes.Binary);
            dsStructure = new Mat();
            Cv2.Resize(reference.StructureMask, dsStructure, dsSize, 0d, 0d, InterpolationFlags.Nearest);
            _currentReciprocalScale = new ReciprocalScaleContext
            { ReferenceScale = baselineScale, StructureMask = dsStructure };
        }

        try
        {
            var searchTimer = Stopwatch.StartNew();
            var distanceEdges = dsEdges ?? reference.Edges;
            using var referenceDistance = MapStructureScaleSearch.CreateDistanceMapFromEdges(
                distanceEdges, tuning.DistanceClipPixels);

            var scaleSearchRadius = request.TrackingMode
                ? Math.Max(
                    tuning.TrackingScaleSearchRadius,
                    StructureRegistrationRules.TrackingScaleSearchRadius)
                : Math.Max(
                    tuning.ScaleSearchRadius,
                    StructureRegistrationRules.ScaleSearchRadius);
            var hypotheses = MapStructureScaleSearch.BuildScaleHypotheses(
                effectiveBaseline,
                request.ScaleSearchPolicy == MapScaleSearchPolicy.Search,
                scaleSearchRadius,
                tuning.ScaleSearchStep);

            var ctx = new MapStructureScaleSearch.ScaleSearchContext();
            Mat? bestHeatmap = null;
            QueryGeometry? bestQuery = null;
            QueryGeometry? diagnosticQuery = null;

            foreach (var scale in hypotheses)
            {
                if (searchTimer.ElapsedMilliseconds >= tuning.StructureFallbackBudgetMilliseconds)
                { ctx.TimeBudgetExceeded = true; break; }
                if (!tuning.DisableScaleEarlyTermination
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
                if (query.Bounds.Width >= refEdgesForCheck.Width
                    || query.Bounds.Height >= refEdgesForCheck.Height)
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
                    MapStructureScaleSearch.SearchRestrictedBranch(
                        query, reference, referenceDistance, live,
                        request, scale, expected, tuning, _currentReciprocalScale, ctx);
                }
                else
                {
                    MapStructureScaleSearch.SearchGlobalBranch(
                        query, reference, referenceDistance, live,
                        request, scale, expected, tuning,
                        _currentReciprocalScale, isReciprocalScale, ctx);
                }

                if (searchTimer.ElapsedMilliseconds >= tuning.StructureFallbackBudgetMilliseconds)
                { ctx.TimeBudgetExceeded = true; break; }

                var scaleBest = ctx.Candidates
                    .Where(c => Math.Abs(c.Scale - scale) < StructureRegistrationRules.ScaleDuplicateTolerance)
                    .OrderBy(c => c.CompositeCost).FirstOrDefault();
                if (scaleBest is not null
                    && (bestQuery is null
                        || scaleBest.CompositeCost < ctx.Candidates
                            .Where(c => Math.Abs(c.Scale - bestQuery.Scale) < StructureRegistrationRules.ScaleDuplicateTolerance)
                            .Min(c => c.CompositeCost)))
                {
                    bestHeatmap?.Dispose(); bestHeatmap = null;
                    bestQuery?.Dispose(); bestQuery = query.CloneForDebug();
                }
            }
            searchTimer.Stop();

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
                    ["usedFastStrategy"] = false,
                    ["usedRestrictedSearch"] = request.RestrictSearchToLockedTransform,
                    ["timeBudgetExceeded"] = ctx.TimeBudgetExceeded,
                    ["queryConstructionMs"] = ctx.QueryConstructionMs,
                    ["historyCandidateMs"] = ctx.HistoryCandidateMs,
                    ["visibleAwareSearchMs"] = ctx.VisibleAwareTotalMs,
                    ["featureVotingMs"] = ctx.FeatureVotingMs,
                    ["pyramidSearchMs"] = ctx.PyramidSearchMs,
                    ["localTemplateSearchMs"] = ctx.LocalTemplateSearchMs,
                    ["globalTemplateSearchMs"] = ctx.GlobalTemplateSearchMs,
                    ["referenceWidth"] = reference.Edges.Width,
                    ["referenceHeight"] = reference.Edges.Height,
                    ["queryEdgePixels"] = diagnosticQuery?.EdgeCount ?? 0,
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
                // ── 排名 + 去重 ──
                var rankingTimer = Stopwatch.StartNew();
                var ranked = MapStructureCandidateCollector.DistinctCandidates(
                        ctx.Candidates, tuning, request.LockedTransform)
                    .OrderBy(c => c.CompositeCost)
                    .ThenBy(c => MapStructureEvaluator.Distance(
                        c.OffsetX, c.OffsetY,
                        request.LockedTransform.OffsetX, request.LockedTransform.OffsetY))
                    .Take(tuning.TopCandidateCount).ToArray();
                MapStructureDebugOutput.WriteSearchDebug(
                    debugDirectory, reference, bestHeatmap, bestQuery, ranked);
                rankingTimer.Stop();

                // ── 诊断数据包（供后续所有 BuildLegacyResult 调用复用）──
                var d = new MapStructureValidator.LegacyDiagnostics(
                    ctx,
                    PreprocessMs: preprocessTimer.Elapsed.TotalMilliseconds,
                    SearchMs: searchTimer.Elapsed.TotalMilliseconds,
                    CandidateRankingMs: rankingTimer.Elapsed.TotalMilliseconds,
                    DebugDirectory: debugDirectory,
                    LockedScale: baselineScale,
                    ReferenceWidth: reference.Edges.Width,
                    ReferenceHeight: reference.Edges.Height,
                    QueryEdgePixels: diagnosticQuery?.EdgeCount ?? 0,
                    QueryBounds: diagnosticQuery?.Bounds,
                    ScaleHypothesisCount: hypotheses.Count,
                    OversizedHypothesisCount: ctx.OversizedHypotheses,
                    UsedRestrictedSearch: request.RestrictSearchToLockedTransform,
                    VisibleMaskMs: live.DiagnosticTiming?.VisibleMaskMs ?? 0d);

                if (ranked.Length == 0)
                {
                    var reason = ctx.TimeBudgetExceeded
                        ? MapStructureRejectionReason.TimeBudgetExceeded
                        : ctx.SufficientlyStructuredHypotheses == 0
                            ? MapStructureRejectionReason.InsufficientStructure
                            : ctx.OversizedHypotheses == ctx.SufficientlyStructuredHypotheses
                                ? MapStructureRejectionReason.QueryLargerThanReference
                                : MapStructureRejectionReason.NoCandidate;
                    MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                        $"结构配准未通过：{reason.ToDisplayText()}");
                    return MapStructureValidator.BuildLegacyResult(reason, d, candidates: ranked);
                }

                // ── 精修 ──
                var refineTimer = Stopwatch.StartNew();
                var forcedRefinementFallback = false;
                var refined = MapStructureRefiner.CanSkipLocalRefinement(
                        ranked, tuning, request.RestrictSearchToLockedTransform)
                    ? ranked[0]
                    : MapStructureRefiner.RefineCandidate(ranked[0], live, reference,
                        referenceDistance, request, tuning, _currentReciprocalScale);
                refineTimer.Stop();
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    $"ECC精修完成 · 收敛={refined.EccConverged}",
                    elapsedMs: refineTimer.Elapsed.TotalMilliseconds,
                    details: new() { ["eccConverged"] = refined.EccConverged, ["eccCorrelation"] = refined.EccCorrelation });

                if (refined.CompositeCost > ranked[0].CompositeCost + StructureRegistrationRules.RefinementWorsenTolerance)
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

                // ── 最终排名 + 验证 ──
                var finalRanked = new[] { refined }.Concat(ranked.Skip(1))
                    .OrderBy(c => c.CompositeCost).ToArray();
                var best = finalRanked[0];
                var secondScore = finalRanked.Length > 1
                    ? finalRanked[1].CompositeCost : double.PositiveInfinity;
                var margin = double.IsPositiveInfinity(secondScore) ? 1d
                    : Math.Clamp((secondScore - best.CompositeCost)
                        / Math.Max(StructureRegistrationRules.MarginNormalizationFloor, secondScore), 0d, 1d);
                var requiredMargin = tuning.MinimumCandidateMargin
                    * (best.UsedGlobalSearch ? StructureRegistrationRules.GlobalSearchMarginMultiplier : 1d);
                var rejection = MapStructureValidator.Validate(
                    best, margin, requiredMargin, tuning,
                    restrictedSearch: request.RestrictSearchToLockedTransform);
                // scale 一致性门：拒绝与锁定/先验 scale 差异过大的候选（根因②'）。
                // 结构配准的 chamfer/edgeCoverage 均单向按 query 归一化，错误的
                // 更大 scale（更小 query）可能在这些指标上反而更漂亮而通过验收；
                // 以锁定 scale 为锚，偏离超过 MaximumScaleChangeRatio 即拒。
                if (rejection == MapStructureRejectionReason.None
                    && !request.ForceBestCandidate
                    && double.IsFinite(request.LockedTransform.ScaleX)
                    && request.LockedTransform.ScaleX > 0d
                    && Math.Abs((best.Scale / request.LockedTransform.ScaleX) - 1d)
                        > StructureRegistrationRules.MaximumScaleChangeRatio)
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

                // ── 构建接受结果 ──
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
            dsEdges?.Dispose();
            dsStructure?.Dispose();
            // The reciprocal context owns dsStructure only for this
            // RegisterLegacy invocation.  Do not leave a disposed Mat in the
            // shared context for a subsequent fast/legacy pass.
            _currentReciprocalScale = ReciprocalScaleContext.None;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 快速粗搜索实验路径
    // ═══════════════════════════════════════════════════════════════

    private MapStructureRegistrationResult TryFastCoarseAlign(
        MapStructureRegistrationRequest request)
    {
        var tuning = request.Tuning.Clone();
        tuning.Normalize();

        var vr = MapStructureValidator.ValidateRequest(request, usedRestrictedSearch: false);
        if (vr is not null) return vr;

        var baselineScale = request.LockedTransform.ScaleX;

        var preprocessTimerFast = Stopwatch.StartNew();
        using var ownedReferenceFast = request.PreparedReference is null
            ? _preprocessor.Process(request.ReferenceImage) : null;
        var referenceFast = request.PreparedReference ?? ownedReferenceFast!;
        using var ownedLiveFast = request.PreparedLive is null
            ? _preprocessor.ProcessLiveRoi(request.LiveRoi, request.LiveIgnoreRegions,
                request.DynamicIgnoreRegions, generateVisibleMask: false)
            : null;
        var liveFast = request.PreparedLive ?? ownedLiveFast!;
        preprocessTimerFast.Stop();

        // ── 互逆参考图缩放 ──
        var effectiveBaseline = baselineScale;
        Mat? dsEdgesFast = null, dsStructureFast = null;
        if (baselineScale < 1.0)
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
            var distanceEdges = dsEdgesFast ?? referenceFast.Edges;
            using var referenceDistance = MapStructureScaleSearch.CreateDistanceMapFromEdges(
                distanceEdges, tuning.DistanceClipPixels);
            var preprocessMs = preprocessTimerFast.Elapsed.TotalMilliseconds;
            var coarseTimer = Stopwatch.StartNew();
            var candidates = new List<MapStructureCandidate>();

            using var query = MapStructureScaleSearch.CreateQuery(
                liveFast, request.LiveRoi.Size(), effectiveBaseline,
                includeVisibleMask: false);
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

            MapStructureScaleSearch.CollectFastCoarseCandidates(
                query, referenceFast, referenceDistance,
                request, effectiveBaseline, tuning, _currentReciprocalScale, candidates);
            MapStructureCandidateCollector.CollectHistoryCandidates(
                query, referenceFast, referenceDistance,
                request, effectiveBaseline, tuning, _currentReciprocalScale, candidates);
            coarseTimer.Stop();
            var coarseMs = coarseTimer.Elapsed.TotalMilliseconds;

            var ranked = MapStructureCandidateCollector.DistinctCandidates(
                    candidates, tuning, request.LockedTransform)
                .OrderBy(c => c.CompositeCost)
                .ThenBy(c => MapStructureEvaluator.Distance(
                    c.OffsetX, c.OffsetY,
                    request.LockedTransform.OffsetX, request.LockedTransform.OffsetY))
                .Take(tuning.TopCandidateCount).ToArray();

            if (ranked.Length == 0)
            {
                return MapStructureValidator.BuildResult(
                    MapStructureRejectionReason.NoCandidate, candidates: ranked,
                    preprocessMs: preprocessMs,
                    searchMs: coarseMs, usedFastStrategy: true,
                    fastCoarseSearchMs: coarseMs, fastCoarseCandidateCount: candidates.Count,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds);
            }

            var refineTimer = Stopwatch.StartNew();
            var refined = MapStructureRefiner.RefineCandidate(
                ranked[0], liveFast, referenceFast, referenceDistance,
                request, tuning, _currentReciprocalScale);
            refineTimer.Stop();

            if (refined.CompositeCost > ranked[0].CompositeCost + StructureRegistrationRules.RefinementWorsenTolerance
                && !request.ForceBestCandidate)
            {
                return MapStructureValidator.BuildResult(
                    MapStructureRejectionReason.RefinementFailed, candidates: ranked,
                    preprocessMs: preprocessMs,
                    searchMs: coarseMs, refineMs: refineTimer.Elapsed.TotalMilliseconds,
                    usedFastStrategy: true,
                    fastCoarseSearchMs: coarseMs, fastCoarseCandidateCount: candidates.Count,
                    lockedScale: baselineScale,
                    referenceWidth: refEdgesForCheck.Width,
                    referenceHeight: refEdgesForCheck.Height,
                    queryEdgePixels: query.EdgeCount,
                    queryBounds: query.Bounds);
            }

            var finalRanked = new[] { refined }.Concat(ranked.Skip(1))
                .OrderBy(c => c.CompositeCost).ToArray();
            var best = finalRanked[0];
            var secondScore = finalRanked.Length > 1
                ? finalRanked[1].CompositeCost : double.PositiveInfinity;
            var margin = double.IsPositiveInfinity(secondScore) ? 1d
                : Math.Clamp((secondScore - best.CompositeCost)
                    / Math.Max(StructureRegistrationRules.MarginNormalizationFloor, secondScore), 0d, 1d);
            var requiredMargin = tuning.MinimumCandidateMargin
                * (best.UsedGlobalSearch ? StructureRegistrationRules.GlobalSearchMarginMultiplier : 1d);
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
                    // Fast coarse search is an early-accept path.  A candidate
                    // can pass broad gates and still be the wrong corridor when
                    // its geometry is weak. Defer it to the full legacy search.
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
            dsEdgesFast?.Dispose();
            dsStructureFast?.Dispose();
            // TryFastCoarseAlign may fall back to Legacy in the same
            // Register call.  Clear the context after disposing its Mat so
            // Legacy cannot reuse the disposed downsampled mask.
            _currentReciprocalScale = ReciprocalScaleContext.None;
        }
    }

    private static Dictionary<string, object?> CreateConfidenceLogDetails(
        MapStructureConfidenceBreakdown breakdown) => new()
        {
            ["confidence"] = breakdown.LockConfidence,
            ["chamferQuality"] = breakdown.ChamferQuality,
            ["edgeCoverage"] = breakdown.EdgeCoverage,
            ["occupancyCoverage"] = breakdown.OccupancyCoverage,
            ["partitionQuality"] = breakdown.PartitionQuality,
            ["geometricFitQuality"] = breakdown.GeometricFitQuality,
            ["evidenceConfidence"] = breakdown.EvidenceConfidence,
            ["geometricLockConfidence"] = breakdown.GeometricLockConfidence,
            ["lockConfidence"] = breakdown.LockConfidence,
            ["candidateMargin"] = breakdown.CandidateSeparation,
            ["hardGateFailure"] = breakdown.HardGateFailure,
            ["lowEvidenceReason"] = breakdown.LowEvidenceReason
        };
}
