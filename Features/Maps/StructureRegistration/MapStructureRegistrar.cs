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

    public MapStructureRegistrar(MapStructurePreprocessor preprocessor)
    {
        _preprocessor = preprocessor;
    }

    public MapStructureRegistrationResult Register(
        MapStructureRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tuning = request.Tuning.Clone();
        tuning.Normalize();

        // Fast 通道由 EnableFastAlignment 开关统一控制，
        // 不再因 tracking 模式或受限搜索范围自动禁用。
        const bool canUseFast = true;

        // ── Shadow Mode：对比 Fast vs Legacy，只返回 Legacy ──
        // Must come BEFORE the production EnableFastAlignment branch so that:
        //   1. Fast does not prematurely return before Shadow runs.
        //   2. Fast executes at most once (not once in production + once in shadow).
        if (tuning.FastAlignmentShadowMode && canUseFast)
        {
            var legacyResult = RegisterLegacy(request);

            try
            {
                var shadowFast = TryFastCoarseAlign(request);
                var fastTransform = shadowFast.Transform;
                var legacyTransform = legacyResult.Transform;
                var transformDelta = 0d;
                var scaleDelta = 0d;
                if (fastTransform is not null && legacyTransform is not null)
                {
                    transformDelta = Math.Sqrt(
                        Math.Pow(fastTransform.OffsetX - legacyTransform.OffsetX, 2d)
                        + Math.Pow(fastTransform.OffsetY - legacyTransform.OffsetY, 2d));
                    scaleDelta = Math.Abs(
                        fastTransform.ScaleX - legacyTransform.ScaleX);
                }
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"Shadow对比 · Fast={(shadowFast.Accepted ? "通过" : "未通过")} "
                    + $"Legacy={(legacyResult.Accepted ? "通过" : "未通过")} "
                    + $"Δ={transformDelta:F1}px Δs={scaleDelta:F4}",
                    details: new()
                    {
                        ["fastAccepted"] = shadowFast.Accepted,
                        ["legacyAccepted"] = legacyResult.Accepted,
                        ["transformDeltaPx"] = transformDelta,
                        ["scaleDelta"] = scaleDelta,
                        ["fastTotalMs"] = shadowFast.SearchMilliseconds
                            + shadowFast.RefineMilliseconds,
                        ["legacyTotalMs"] = legacyResult.SearchMilliseconds
                            + legacyResult.RefineMilliseconds,
                        ["fastRejection"] = shadowFast.RejectionReason.ToString(),
                        ["legacyRejection"] = legacyResult.RejectionReason.ToString(),
                    });
            }
            catch
            {
                // Shadow 模式下的异常不应影响生产结果
            }

            return legacyResult;
        }

        // ── 生产快速对齐分支 ──
        if (tuning.EnableFastAlignment && canUseFast)
        {
            try
            {
                var fastResult = TryFastCoarseAlign(request);
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    fastResult.Accepted ? MapLogLevel.Info : MapLogLevel.Warning,
                    $"快速粗搜索{(fastResult.Accepted ? "通过" : "未通过")}",
                    elapsedMs: fastResult.SearchMilliseconds
                        + fastResult.RefineMilliseconds,
                    details: new()
                    {
                        ["usedFastStrategy"] = true,
                        ["accepted"] = fastResult.Accepted,
                        ["fastCoarseMs"] = fastResult.FastCoarseSearchMilliseconds,
                        ["fastCandidates"] = fastResult.FastCoarseCandidateCount,
                        ["rejection"] = fastResult.RejectionReason.ToString()
                    });
                if (fastResult.Accepted)
                    return fastResult;
                if (!tuning.FastFallbackToLegacy)
                    return fastResult;
            }
            catch (Exception ex)
            {
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Error,
                    $"快速粗搜索异常，回退 Legacy：{ex.Message}");
                // 异常时继续执行 RegisterLegacy
            }
        }

        return RegisterLegacy(request);
    }

    private MapStructureRegistrationResult RegisterLegacy(
        MapStructureRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tuning = request.Tuning.Clone();
        tuning.Normalize();
        if (request.ReferenceImage.Empty()
            || request.LiveRoi.Empty()
            || !request.ViewportBounds.IsValid)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InvalidInput,
                usedRestrictedSearch:
                    request.RestrictSearchToLockedTransform);
        }
        if (request.LockedTransform.AlignmentMode != MapOverlayAlignmentMode.Uniform
            || Math.Abs(
                request.LockedTransform.ScaleX
                - request.LockedTransform.ScaleY) > 0.0001d)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.UnsupportedAlignmentMode,
                usedRestrictedSearch:
                    request.RestrictSearchToLockedTransform);
        }
        var normalizedRotation =
            ((request.FixedRotationDegrees % 360d) + 360d) % 360d;
        if (Math.Min(
                normalizedRotation,
                Math.Abs(360d - normalizedRotation)) > 0.1d)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.UnsupportedAlignmentMode,
                "当前结构配准仅支持已标定的 0° 原生地图旋转。",
                usedRestrictedSearch:
                    request.RestrictSearchToLockedTransform);
        }

        var baselineScale = request.LockedTransform.ScaleX;
        if (!double.IsFinite(baselineScale)
            || baselineScale <= StructureRegistrationRules.MinimumUsableScale)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InvalidLockedScale,
                usedRestrictedSearch:
                    request.RestrictSearchToLockedTransform);
        }

        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "开始结构配准",
            details: new() { ["allowScaleSearch"] = request.AllowScaleSearch, ["trackingMode"] = request.TrackingMode });

        var preprocessTimer = Stopwatch.StartNew();
        using var ownedReference = request.PreparedReference is null
            ? _preprocessor.Process(request.ReferenceImage)
            : null;
        var reference = request.PreparedReference ?? ownedReference!;
        using var ownedLive = request.PreparedLive is null
            ? _preprocessor.ProcessLiveRoi(
                request.LiveRoi,
                request.LiveIgnoreRegions,
                request.DynamicIgnoreRegions,
                generateVisibleMask: tuning.EnableVisibleMask)
            : null;
        var live = request.PreparedLive ?? ownedLive!;
        preprocessTimer.Stop();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "结构预处理完成",
            elapsedMs: preprocessTimer.Elapsed.TotalMilliseconds);
        var debugDirectory = tuning.EnableDebugOutput
            ? ResolveDebugDirectory(request.DebugOutputDirectory)
            : null;
        WritePreprocessDebug(
            debugDirectory,
            request.LiveRoi,
            live,
            reference);

        var searchTimer = Stopwatch.StartNew();
        var distanceMapTimer = Stopwatch.StartNew();
        var referenceDistance = CreateDistanceMap(
            reference,
            tuning.DistanceClipPixels);
        distanceMapTimer.Stop();
        var scaleSearchRadius = request.TrackingMode
            ? tuning.TrackingScaleSearchRadius
            : tuning.ScaleSearchRadius;
        var hypotheses = BuildScaleHypotheses(
            baselineScale,
            request.AllowScaleSearch,
            scaleSearchRadius,
            tuning.ScaleSearchStep);
        var candidates = new List<MapStructureCandidate>();
        Mat? bestHeatmap = null;
        QueryGeometry? bestQuery = null;
        QueryGeometry? diagnosticQuery = null;
        var sufficientlyStructuredHypotheses = 0;
        var oversizedHypotheses = 0;
        var featureMatchCount = 0;
        var featureInlierCount = 0;
        var queryConstructionMs = 0d;
        var historyCandidateMs = 0d;
        var featureVotingMs = 0d;
        var pyramidSearchMs = 0d;
        var localTemplateSearchMs = 0d;
        var globalTemplateSearchMs = 0d;
        var timeBudgetExceeded = false;
        // Visible-aware 诊断累加器
        var visibleAwareTotalMs = 0d;
        var visibleAwareCandidateCount = 0;
        var visibleAwareBestCost = double.PositiveInfinity;
        var visibleAwareSecondCost = double.PositiveInfinity;
        var visibleAwareBestHypothesisScale = 0d;
        double? visibleAwareVisibleFraction = null;
        int? visibleAwareStructurePixels = null;
        int? visibleAwareEdgePixels = null;
        var visibleAwareEarlyAccepted = false;
        string? visibleAwareFallbackReason = null;
        var skipLegacyCandidates = false;  // Phase 5: 提前终止标志

        foreach (var scale in hypotheses)
        {
            if (searchTimer.ElapsedMilliseconds
                >= tuning.StructureFallbackBudgetMilliseconds)
            {
                timeBudgetExceeded = true;
                break;
            }
            // Early termination: if we already have a strong candidate, stop.
            if (candidates.Count > 0 && candidates[0].CompositeCost
                <= tuning.EarlyTerminationScoreThreshold)
            {
                break;
            }
            var queryTimer = Stopwatch.StartNew();
            using var query = CreateQuery(
                live, request.LiveRoi.Size(), scale,
                includeVisibleMask: tuning.EnableVisibleAwareShadow
                    || tuning.EnableVisibleAwareInjection);
            queryTimer.Stop();
            queryConstructionMs += queryTimer.Elapsed.TotalMilliseconds;
            diagnosticQuery ??= query.CloneForDebug();
            if (query.EdgeCount < tuning.MinimumEdgePixels
                || query.Bounds.Width < tuning.MinimumSpanPixels
                || query.Bounds.Height < tuning.MinimumSpanPixels)
            {
                continue;
            }
            sufficientlyStructuredHypotheses++;
            if (query.Bounds.Width >= reference.Edges.Width
                || query.Bounds.Height >= reference.Edges.Height)
            {
                oversizedHypotheses++;
                continue;
            }

            var expected = ExpectedReferenceLocation(
                request,
                scale,
                query.Bounds);
            var historyTimer = Stopwatch.StartNew();
            CollectHistoryCandidates(
                query,
                reference,
                referenceDistance,
                request,
                scale,
                tuning,
                candidates);
            historyTimer.Stop();
            historyCandidateMs += historyTimer.Elapsed.TotalMilliseconds;
            using var scores = new Mat();
            if (request.RestrictSearchToLockedTransform)
            {
                // ===== Restricted 路径：ORB + 受限搜索 =====
                if (tuning.EnableFeatureVoting)
                {
                    var featureTimer = Stopwatch.StartNew();
                    CollectFeatureCandidates(
                        live,
                        reference,
                        query,
                        request,
                        scale,
                        tuning,
                        candidates,
                        out var matches,
                        out var inliers);
                    featureTimer.Stop();
                    featureVotingMs += featureTimer.Elapsed.TotalMilliseconds;
                    featureMatchCount = Math.Max(featureMatchCount, matches);
                    featureInlierCount = Math.Max(featureInlierCount, inliers);
                }
                var scoreDomain = new Size(
                    referenceDistance.Width - query.Bounds.Width + 1,
                    referenceDistance.Height - query.Bounds.Height + 1);
                var searchRadiusPixels = request.TrackingMode
                    ? tuning.TrackingSearchRadiusPixels
                    : tuning.PreviousAlignmentSearchRadiusPixels;
                var radiusInReferencePixels = Math.Max(
                    tuning.MinimumSpanPixels,
                    (int)Math.Ceiling(searchRadiusPixels / scale));
                var restrictedDomain = CenteredSearchRect(
                    scoreDomain,
                    expected.X,
                    expected.Y,
                    radiusInReferencePixels);
                SearchRestrictedCandidates(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    expected,
                    restrictedDomain,
                    tuning,
                    candidates);
            }
            else
            {
                // ===== 全局搜索分支：Visible-aware → ORB → Pyramid → Legacy =====

                // Step 1: Visible-aware 快速候选
                var visibleAwareSw = Stopwatch.StartNew();
                var vaDiag = CollectVisibleAwareCandidates(
                    query, reference, referenceDistance,
                    request, scale, tuning, candidates);
                visibleAwareSw.Stop();
                if (vaDiag.Ran)
                {
                    visibleAwareTotalMs += visibleAwareSw.Elapsed.TotalMilliseconds;
                    visibleAwareCandidateCount += vaDiag.CandidateCount;
                    visibleAwareVisibleFraction ??= vaDiag.VisibleFraction;
                    visibleAwareStructurePixels ??= vaDiag.VisibleStructurePixels;
                    visibleAwareEdgePixels ??= vaDiag.VisibleEdgePixels;
                    if (vaDiag.BestCost < visibleAwareBestCost)
                    {
                        visibleAwareBestCost = vaDiag.BestCost;
                        visibleAwareBestHypothesisScale = scale;
                        visibleAwareSecondCost = vaDiag.SecondCost;
                    }
                }

                // Step 2: 判断是否提前终止
                if (tuning.EnableVisibleAwareEarlyExit
                    && tuning.VisibleAwareEarlyTerminationMaxCompositeCost > 0d)
                {
                    var visibleAwareCandidates = candidates
                        .Where(c => c.FromVisibleAware)
                        .OrderBy(c => c.CompositeCost)
                        .ToArray();
                    var visibleAwareBest = visibleAwareCandidates.FirstOrDefault();
                    if (visibleAwareBest is not null
                        && visibleAwareBest.CompositeCost
                            <= tuning.VisibleAwareEarlyTerminationMaxCompositeCost)
                    {
                        var secondBestCost = visibleAwareCandidates.Length > 1
                            ? visibleAwareCandidates[1].CompositeCost
                            : double.PositiveInfinity;
                        if (MeetsEarlyTerminationCriteria(
                                visibleAwareBest, secondBestCost, tuning))
                        {
                            skipLegacyCandidates = true;
                            visibleAwareEarlyAccepted = true;
                        }
                        else
                        {
                            visibleAwareFallbackReason =
                                "Visible-aware best below cost threshold but fails " +
                                "individual validation criteria";
                        }
                    }
                    else if (visibleAwareBest is not null)
                    {
                        visibleAwareFallbackReason =
                            $"Visible-aware best composite cost " +
                            $"{visibleAwareBest.CompositeCost:F3} exceeds threshold " +
                            $"{tuning.VisibleAwareEarlyTerminationMaxCompositeCost:F3}";
                    }
                    else
                    {
                        visibleAwareFallbackReason =
                            "No visible-aware candidates found for early termination";
                    }
                }

                // Step 3: Legacy 候选生成（由 skipLegacyCandidates 门控）
                if (!skipLegacyCandidates)
                {
                    var bestFastCost = candidates.Count == 0
                        ? double.PositiveInfinity
                        : candidates.Min(candidate => candidate.CompositeCost);
                    var shouldRunFeatureVoting = tuning.EnableFeatureVoting
                        && (candidates.Count == 0
                            || bestFastCost > tuning.EarlyTerminationScoreThreshold);
                    if (shouldRunFeatureVoting)
                    {
                        var featureTimer = Stopwatch.StartNew();
                        CollectFeatureCandidates(
                            live,
                            reference,
                            query,
                            request,
                            scale,
                            tuning,
                            candidates,
                            out var matches,
                            out var inliers);
                        featureTimer.Stop();
                        featureVotingMs += featureTimer.Elapsed.TotalMilliseconds;
                        featureMatchCount = Math.Max(featureMatchCount, matches);
                        featureInlierCount = Math.Max(featureInlierCount, inliers);
                    }
                    var pyramidTimer = Stopwatch.StartNew();
                    CollectPyramidCandidates(
                        query,
                        reference,
                        referenceDistance,
                        request,
                        scale,
                        tuning,
                        candidates);
                    pyramidTimer.Stop();
                    pyramidSearchMs += pyramidTimer.Elapsed.TotalMilliseconds;
                }
                if (!skipLegacyCandidates)
                {
                    using var template = new Mat(query.Edges, query.Bounds);
                    using var templateFloat = new Mat();
                    template.ConvertTo(
                        templateFloat,
                        MatType.CV_32FC1,
                        1d / 255d);
                    var localRadius = Math.Max(
                        tuning.MinimumSpanPixels,
                        (int)Math.Round(
                            Math.Max(reference.Edges.Width, reference.Edges.Height)
                            * tuning.LocalSearchRadiusRatio));
                    var templateTimer = Stopwatch.StartNew();
                    Cv2.MatchTemplate(
                        referenceDistance,
                        templateFloat,
                        scores,
                        TemplateMatchModes.CCorr);
                    Cv2.Multiply(
                        scores,
                        1d / Math.Max(1, query.EdgeCount),
                        scores);
                    templateTimer.Stop();
                    var localRect = CenteredSearchRect(
                        scores.Size(),
                        expected.X,
                        expected.Y,
                        localRadius);
                    var localCandidateTimer = Stopwatch.StartNew();
                    CollectCandidates(
                        scores,
                        query,
                        reference,
                        referenceDistance,
                        request,
                        scale,
                        localRect,
                        usedGlobalSearch: false,
                        tuning,
                        candidates);
                    localCandidateTimer.Stop();
                    localTemplateSearchMs += localCandidateTimer.Elapsed.TotalMilliseconds;
                    var globalCandidateTimer = Stopwatch.StartNew();
                    CollectCandidates(
                        scores,
                        query,
                        reference,
                        referenceDistance,
                        request,
                        scale,
                        new Rect(0, 0, scores.Width, scores.Height),
                        usedGlobalSearch: true,
                        tuning,
                        candidates);
                    globalCandidateTimer.Stop();
                    globalTemplateSearchMs += templateTimer.Elapsed.TotalMilliseconds
                        + globalCandidateTimer.Elapsed.TotalMilliseconds;
                }
            }

            if (searchTimer.ElapsedMilliseconds
                >= tuning.StructureFallbackBudgetMilliseconds)
            {
                timeBudgetExceeded = true;
                break;
            }

            var scaleBest = candidates
                .Where(candidate => Math.Abs(candidate.Scale - scale) < 0.000001d)
                .OrderBy(candidate => candidate.CompositeCost)
                .FirstOrDefault();
            if (scaleBest is not null
                && (bestQuery is null
                    || scaleBest.CompositeCost
                        < candidates
                            .Where(candidate => Math.Abs(
                                candidate.Scale - bestQuery.Scale) < 0.000001d)
                            .Min(candidate => candidate.CompositeCost)))
            {
                bestHeatmap?.Dispose();
                if (!request.RestrictSearchToLockedTransform)
                    bestHeatmap = scores.Clone();
                bestQuery?.Dispose();
                bestQuery = query.CloneForDebug();
            }
        }
        searchTimer.Stop();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            $"结构搜索完成 · {candidates.Count} 个候选",
            elapsedMs: searchTimer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["candidateCount"] = candidates.Count,
                ["hypotheses"] = hypotheses.Count,
                ["sufficientlyStructuredHypotheses"] = sufficientlyStructuredHypotheses,
                ["oversizedHypotheses"] = oversizedHypotheses,
                ["bestScore"] = candidates.Count > 0
                    ? candidates.Min(c => c.CompositeCost) : -1d,
                ["usedFastStrategy"] = false,
                ["usedRestrictedSearch"] = request.RestrictSearchToLockedTransform,
                ["timeBudgetExceeded"] = timeBudgetExceeded,
                ["distanceMapMs"] = distanceMapTimer.Elapsed.TotalMilliseconds,
                ["queryConstructionMs"] = queryConstructionMs,
                ["historyCandidateMs"] = historyCandidateMs,
                ["visibleAwareSearchMs"] = visibleAwareTotalMs,
                ["featureVotingMs"] = featureVotingMs,
                ["pyramidSearchMs"] = pyramidSearchMs,
                ["localTemplateSearchMs"] = localTemplateSearchMs,
                ["globalTemplateSearchMs"] = globalTemplateSearchMs,
                ["visibleAwareEarlyAccepted"] = visibleAwareEarlyAccepted,
                ["visibleAwareFallbackReason"] = visibleAwareFallbackReason
            });

        try
        {
            var rankingTimer = Stopwatch.StartNew();
            var ranked = DistinctCandidates(
                    candidates,
                    tuning,
                    request.LockedTransform)
                .OrderBy(candidate => candidate.CompositeCost)
                .ThenBy(candidate => Distance(
                    candidate.OffsetX,
                    candidate.OffsetY,
                    request.LockedTransform.OffsetX,
                    request.LockedTransform.OffsetY))
                .Take(tuning.TopCandidateCount)
                .ToArray();
            WriteSearchDebug(
                debugDirectory,
                reference,
                bestHeatmap,
                bestQuery,
                ranked);
            rankingTimer.Stop();
            if (ranked.Length == 0)
            {
                var reason = timeBudgetExceeded
                    ? MapStructureRejectionReason.TimeBudgetExceeded
                    : sufficientlyStructuredHypotheses == 0
                    ? MapStructureRejectionReason.InsufficientStructure
                    : oversizedHypotheses == sufficientlyStructuredHypotheses
                        ? MapStructureRejectionReason.QueryLargerThanReference
                        : MapStructureRejectionReason.NoCandidate;
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                    $"结构配准未通过：{reason.ToDisplayText()}");
                return MapStructureRegistrationResult.Reject(
                    reason,
                    candidates: ranked,
                    preprocessMilliseconds: preprocessTimer.Elapsed.TotalMilliseconds,
                    searchMilliseconds: searchTimer.Elapsed.TotalMilliseconds,
                    debugOutputDirectory: debugDirectory,
                    lockedScale: baselineScale,
                    referenceWidth: reference.Edges.Width,
                    referenceHeight: reference.Edges.Height,
                    queryEdgePixels: diagnosticQuery?.EdgeCount ?? 0,
                    queryBounds: diagnosticQuery?.Bounds,
                    scaleHypothesisCount: hypotheses.Count,
                    oversizedHypothesisCount: oversizedHypotheses,
                    usedRestrictedSearch:
                        request.RestrictSearchToLockedTransform,
                    visibleMaskMilliseconds: live.DiagnosticTiming?.VisibleMaskMs ?? 0d,
                    visibleFraction: visibleAwareVisibleFraction ?? 0d,
                    visibleStructurePixels: visibleAwareStructurePixels ?? 0,
                    visibleEdgePixels: visibleAwareEdgePixels ?? 0,
                    visibleAwareSearchMilliseconds: visibleAwareTotalMs,
                    visibleAwareCandidateCount: visibleAwareCandidateCount,
                    visibleAwareTopCost: visibleAwareBestCost,
                    visibleAwareTopMargin: double.IsPositiveInfinity(visibleAwareSecondCost)
                        ? 0d : Math.Clamp((visibleAwareSecondCost - visibleAwareBestCost)
                            / Math.Max(0.01d, visibleAwareSecondCost), 0d, 1d),
                    visibleAwareEarlyAccepted: visibleAwareEarlyAccepted,
                    visibleAwareFallbackReason: visibleAwareFallbackReason,
                    distanceMapMilliseconds: distanceMapTimer.Elapsed.TotalMilliseconds,
                    queryConstructionMilliseconds: queryConstructionMs,
                    historyCandidateMilliseconds: historyCandidateMs,
                    featureVotingMilliseconds: featureVotingMs,
                    pyramidSearchMilliseconds: pyramidSearchMs,
                    localTemplateSearchMilliseconds: localTemplateSearchMs,
                    globalTemplateSearchMilliseconds: globalTemplateSearchMs,
                    candidateRankingMilliseconds: rankingTimer.Elapsed.TotalMilliseconds);
            }

            var refineTimer = Stopwatch.StartNew();
            var forcedRefinementFallback = false;
            var refined = CanSkipLocalRefinement(ranked, tuning)
                ? ranked[0]
                : RefineCandidate(
                    ranked[0],
                    live,
                    reference,
                    referenceDistance,
                    request,
                    tuning);
            refineTimer.Stop();
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                $"ECC精修完成 · 收敛={refined.EccConverged}",
                elapsedMs: refineTimer.Elapsed.TotalMilliseconds,
                details: new() { ["eccConverged"] = refined.EccConverged, ["eccCorrelation"] = refined.EccCorrelation });
            if (refined.CompositeCost > ranked[0].CompositeCost + 0.001d)
            {
                if (!request.ForceBestCandidate)
                {
                    MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                        $"结构配准未通过：{MapStructureRejectionReason.RefinementFailed.ToDisplayText()}");
                    return MapStructureRegistrationResult.Reject(
                        MapStructureRejectionReason.RefinementFailed,
                        candidates: ranked,
                        preprocessMilliseconds: preprocessTimer.Elapsed.TotalMilliseconds,
                        searchMilliseconds: searchTimer.Elapsed.TotalMilliseconds,
                        debugOutputDirectory: debugDirectory,
                        usedRestrictedSearch:
                            request.RestrictSearchToLockedTransform,
                        visibleMaskMilliseconds: live.DiagnosticTiming?.VisibleMaskMs ?? 0d,
                        visibleFraction: visibleAwareVisibleFraction ?? 0d,
                        visibleStructurePixels: visibleAwareStructurePixels ?? 0,
                        visibleEdgePixels: visibleAwareEdgePixels ?? 0,
                        visibleAwareSearchMilliseconds: visibleAwareTotalMs,
                        visibleAwareCandidateCount: visibleAwareCandidateCount,
                        visibleAwareTopCost: visibleAwareBestCost,
                        visibleAwareTopMargin: double.IsPositiveInfinity(visibleAwareSecondCost)
                            ? 0d : Math.Clamp((visibleAwareSecondCost - visibleAwareBestCost)
                                / Math.Max(0.01d, visibleAwareSecondCost), 0d, 1d),
                        visibleAwareEarlyAccepted: visibleAwareEarlyAccepted,
                        visibleAwareFallbackReason: visibleAwareFallbackReason,
                        distanceMapMilliseconds: distanceMapTimer.Elapsed.TotalMilliseconds,
                        queryConstructionMilliseconds: queryConstructionMs,
                        historyCandidateMilliseconds: historyCandidateMs,
                        featureVotingMilliseconds: featureVotingMs,
                        pyramidSearchMilliseconds: pyramidSearchMs,
                        localTemplateSearchMilliseconds: localTemplateSearchMs,
                        globalTemplateSearchMilliseconds: globalTemplateSearchMs,
                        candidateRankingMilliseconds: rankingTimer.Elapsed.TotalMilliseconds);
                }
                refined = ranked[0];
                forcedRefinementFallback = true;
            }

            var finalRanked = new[] { refined }
                .Concat(ranked.Skip(1))
                .OrderBy(candidate => candidate.CompositeCost)
                .ToArray();
            var best = finalRanked[0];
            var secondScore = finalRanked.Length > 1
                ? finalRanked[1].CompositeCost
                : double.PositiveInfinity;
            var margin = double.IsPositiveInfinity(secondScore)
                ? 1d
                : Math.Clamp(
                    (secondScore - best.CompositeCost)
                    / Math.Max(0.01d, secondScore),
                    0d,
                    1d);
            var requiredMargin = tuning.MinimumCandidateMargin
                * (best.UsedGlobalSearch ? 1.25d : 1d);
            var rejection = Validate(best, margin, requiredMargin, tuning);
            var forcedReason = forcedRefinementFallback
                ? MapStructureRejectionReason.RefinementFailed
                : rejection;
            var confidenceBreakdown = MapStructureConfidenceCalculator.Calculate(
                best,
                margin,
                tuning,
                rejection,
                isTrackingMode: request.TrackingMode,
                sideEntrancePrior: request.SideEntrancePrior);
            var confidence = confidenceBreakdown.FinalScore;
            if (rejection != MapStructureRejectionReason.None
                && !request.ForceBestCandidate)
            {
                var rejectionDetails = CreateConfidenceLogDetails(
                    confidenceBreakdown);
                rejectionDetails["bestScore"] = best.CompositeCost;
                rejectionDetails["margin"] = margin;
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                    $"结构配准未通过：{rejection.ToDisplayText()}",
                    details: rejectionDetails);
                return new MapStructureRegistrationResult
                {
                    RejectionReason = rejection,
                    FailureReason = rejection.ToDisplayText(),
                    Confidence = confidence,
                    ConfidenceBreakdown = confidenceBreakdown,
                    BestScore = best.CompositeCost,
                    SecondScore = secondScore,
                    CandidateMargin = margin,
                    Candidates = finalRanked,
                    PreprocessMilliseconds = preprocessTimer.Elapsed.TotalMilliseconds,
                    SearchMilliseconds = searchTimer.Elapsed.TotalMilliseconds,
                    RefineMilliseconds = refineTimer.Elapsed.TotalMilliseconds,
                    DistanceMapMilliseconds = distanceMapTimer.Elapsed.TotalMilliseconds,
                    QueryConstructionMilliseconds = queryConstructionMs,
                    HistoryCandidateMilliseconds = historyCandidateMs,
                    FeatureVotingMilliseconds = featureVotingMs,
                    PyramidSearchMilliseconds = pyramidSearchMs,
                    LocalTemplateSearchMilliseconds = localTemplateSearchMs,
                    GlobalTemplateSearchMilliseconds = globalTemplateSearchMs,
                    CandidateRankingMilliseconds = rankingTimer.Elapsed.TotalMilliseconds,
                    DebugOutputDirectory = debugDirectory,
                    LockedScale = baselineScale,
                    ReferenceWidth = reference.Edges.Width,
                    ReferenceHeight = reference.Edges.Height,
                    QueryEdgePixels = diagnosticQuery?.EdgeCount ?? 0,
                    QueryBoundsX = diagnosticQuery?.Bounds.X ?? 0,
                    QueryBoundsY = diagnosticQuery?.Bounds.Y ?? 0,
                    QueryBoundsWidth = diagnosticQuery?.Bounds.Width ?? 0,
                    QueryBoundsHeight = diagnosticQuery?.Bounds.Height ?? 0,
                    ScaleHypothesisCount = hypotheses.Count,
                    OversizedHypothesisCount = oversizedHypotheses,
                    UsedRestrictedSearch =
                        request.RestrictSearchToLockedTransform,
                    FeatureMatchCount = featureMatchCount,
                    FeatureInlierCount = featureInlierCount,
                    FeatureConsensus = best.FeatureConsensus,
                    EccConverged = best.EccConverged,
                    EccCorrelation = best.EccCorrelation,
                    VisibleMaskMilliseconds = live.DiagnosticTiming?.VisibleMaskMs ?? 0d,
                    VisibleFraction = visibleAwareVisibleFraction ?? 0d,
                    VisibleStructurePixels = visibleAwareStructurePixels ?? 0,
                    VisibleEdgePixels = visibleAwareEdgePixels ?? 0,
                    VisibleAwareSearchMilliseconds = visibleAwareTotalMs,
                    VisibleAwareCandidateCount = visibleAwareCandidateCount,
                    VisibleAwareTopCost = visibleAwareBestCost,
                    VisibleAwareTopMargin = double.IsPositiveInfinity(visibleAwareSecondCost)
                        ? 0d : Math.Clamp((visibleAwareSecondCost - visibleAwareBestCost)
                            / Math.Max(0.01d, visibleAwareSecondCost), 0d, 1d),
                    VisibleAwareEarlyAccepted = visibleAwareEarlyAccepted,
                    VisibleAwareFallbackReason = visibleAwareFallbackReason
                };
            }

            var transform = BuildTransform(best, request, reference);
            WriteFinalDebug(
                debugDirectory,
                request,
                reference,
                live,
                transform);
            var acceptedDetails = CreateConfidenceLogDetails(
                confidenceBreakdown);
            acceptedDetails["bestScore"] = best.CompositeCost;
            acceptedDetails["margin"] = margin;
            acceptedDetails["usedFastStrategy"] = false;
            acceptedDetails["scaleHypotheses"] = hypotheses.Count;
            acceptedDetails["visibleAwareEarlyAccepted"] = visibleAwareEarlyAccepted;
            acceptedDetails["visibleAwareFallbackReason"] = visibleAwareFallbackReason;
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                $"结构配准通过 · 置信度 {confidence:P0} · 最佳分数 {best.CompositeCost:F3}",
                details: acceptedDetails);
            return new MapStructureRegistrationResult
            {
                Accepted = true,
                Transform = transform,
                Confidence = confidence,
                ConfidenceBreakdown = confidenceBreakdown,
                BestScore = best.CompositeCost,
                SecondScore = secondScore,
                CandidateMargin = margin,
                RejectionReason = forcedReason,
                FailureReason = forcedReason == MapStructureRejectionReason.None
                    ? string.Empty
                    : forcedReason.ToDisplayText(),
                Candidates = finalRanked,
                PreprocessMilliseconds = preprocessTimer.Elapsed.TotalMilliseconds,
                SearchMilliseconds = searchTimer.Elapsed.TotalMilliseconds,
                RefineMilliseconds = refineTimer.Elapsed.TotalMilliseconds,
                DistanceMapMilliseconds = distanceMapTimer.Elapsed.TotalMilliseconds,
                QueryConstructionMilliseconds = queryConstructionMs,
                HistoryCandidateMilliseconds = historyCandidateMs,
                FeatureVotingMilliseconds = featureVotingMs,
                PyramidSearchMilliseconds = pyramidSearchMs,
                LocalTemplateSearchMilliseconds = localTemplateSearchMs,
                GlobalTemplateSearchMilliseconds = globalTemplateSearchMs,
                CandidateRankingMilliseconds = rankingTimer.Elapsed.TotalMilliseconds,
                DebugOutputDirectory = debugDirectory,
                LockedScale = baselineScale,
                ReferenceWidth = reference.Edges.Width,
                ReferenceHeight = reference.Edges.Height,
                QueryEdgePixels = diagnosticQuery?.EdgeCount ?? 0,
                QueryBoundsX = diagnosticQuery?.Bounds.X ?? 0,
                QueryBoundsY = diagnosticQuery?.Bounds.Y ?? 0,
                QueryBoundsWidth = diagnosticQuery?.Bounds.Width ?? 0,
                QueryBoundsHeight = diagnosticQuery?.Bounds.Height ?? 0,
                ScaleHypothesisCount = hypotheses.Count,
                OversizedHypothesisCount = oversizedHypotheses,
                UsedRestrictedSearch =
                    request.RestrictSearchToLockedTransform,
                WasForcedBestCandidate =
                    forcedReason != MapStructureRejectionReason.None,
                FeatureMatchCount = featureMatchCount,
                FeatureInlierCount = featureInlierCount,
                FeatureConsensus = best.FeatureConsensus,
                EccConverged = best.EccConverged,
                EccCorrelation = best.EccCorrelation,
                VisibleMaskMilliseconds = live.DiagnosticTiming?.VisibleMaskMs ?? 0d,
                VisibleFraction = visibleAwareVisibleFraction ?? 0d,
                VisibleStructurePixels = visibleAwareStructurePixels ?? 0,
                VisibleEdgePixels = visibleAwareEdgePixels ?? 0,
                VisibleAwareSearchMilliseconds = visibleAwareTotalMs,
                VisibleAwareCandidateCount = visibleAwareCandidateCount,
                VisibleAwareTopCost = visibleAwareBestCost,
                VisibleAwareTopMargin = double.IsPositiveInfinity(visibleAwareSecondCost)
                    ? 0d : Math.Clamp((visibleAwareSecondCost - visibleAwareBestCost)
                        / Math.Max(0.01d, visibleAwareSecondCost), 0d, 1d),
                VisibleAwareEarlyAccepted = visibleAwareEarlyAccepted,
                VisibleAwareFallbackReason = visibleAwareFallbackReason
            };
        }
        finally
        {
            bestHeatmap?.Dispose();
            bestQuery?.Dispose();
            diagnosticQuery?.Dispose();
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

        // 输入验证（与 RegisterLegacy 相同的早期检查）
        if (request.ReferenceImage.Empty()
            || request.LiveRoi.Empty()
            || !request.ViewportBounds.IsValid)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InvalidInput,
                usedRestrictedSearch: false);
        }
        if (request.LockedTransform.AlignmentMode != MapOverlayAlignmentMode.Uniform
            || Math.Abs(
                request.LockedTransform.ScaleX
                - request.LockedTransform.ScaleY) > 0.0001d)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.UnsupportedAlignmentMode,
                usedRestrictedSearch: false);
        }
        var normalizedRotation =
            ((request.FixedRotationDegrees % 360d) + 360d) % 360d;
        if (Math.Min(normalizedRotation, Math.Abs(360d - normalizedRotation)) > 0.1d)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.UnsupportedAlignmentMode,
                "当前结构配准仅支持已标定的 0° 原生地图旋转。",
                usedRestrictedSearch: false);
        }

        var baselineScale = request.LockedTransform.ScaleX;
        if (!double.IsFinite(baselineScale)
            || baselineScale <= StructureRegistrationRules.MinimumUsableScale)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InvalidLockedScale,
                usedRestrictedSearch: false);
        }

        // 预处理
        var preprocessTimer = Stopwatch.StartNew();
        using var ownedReference = request.PreparedReference is null
            ? _preprocessor.Process(request.ReferenceImage)
            : null;
        var reference = request.PreparedReference ?? ownedReference!;
        using var ownedLive = request.PreparedLive is null
            ? _preprocessor.ProcessLiveRoi(
                request.LiveRoi,
                request.LiveIgnoreRegions,
                request.DynamicIgnoreRegions,
                generateVisibleMask: false)
            : null;
        var live = request.PreparedLive ?? ownedLive!;
        preprocessTimer.Stop();

        var referenceDistance = CreateDistanceMap(
            reference,
            tuning.DistanceClipPixels);
        var preprocessMs = preprocessTimer.Elapsed.TotalMilliseconds;
        var coarseTimer = Stopwatch.StartNew();
        var candidates = new List<MapStructureCandidate>();

        // Stage 1: 单尺度粗搜索
        using var query = CreateQuery(
            live, request.LiveRoi.Size(), baselineScale,
            includeVisibleMask: false);
        if (query.EdgeCount < tuning.MinimumEdgePixels
            || query.Bounds.Width < tuning.MinimumSpanPixels
            || query.Bounds.Height < tuning.MinimumSpanPixels)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InsufficientStructure,
                preprocessMilliseconds: preprocessMs,
                searchMilliseconds: coarseTimer.Elapsed.TotalMilliseconds);
        }
        if (query.Bounds.Width >= reference.Edges.Width
            || query.Bounds.Height >= reference.Edges.Height)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.QueryLargerThanReference,
                preprocessMilliseconds: preprocessMs,
                searchMilliseconds: coarseTimer.Elapsed.TotalMilliseconds);
        }

        CollectFastCoarseCandidates(
            query, reference, referenceDistance,
            request, baselineScale, tuning, candidates);
        CollectHistoryCandidates(
            query, reference, referenceDistance,
            request, baselineScale, tuning, candidates);
        coarseTimer.Stop();
        var coarseMs = coarseTimer.Elapsed.TotalMilliseconds;

        // Stage 2: 排名 + 去重
        var ranked = DistinctCandidates(
                candidates,
                tuning,
                request.LockedTransform)
            .OrderBy(candidate => candidate.CompositeCost)
            .ThenBy(candidate => Distance(
                candidate.OffsetX,
                candidate.OffsetY,
                request.LockedTransform.OffsetX,
                request.LockedTransform.OffsetY))
            .Take(tuning.TopCandidateCount)
            .ToArray();

        if (ranked.Length == 0)
        {
            return new MapStructureRegistrationResult
            {
                RejectionReason = MapStructureRejectionReason.NoCandidate,
                FailureReason = "快速粗搜索未找到任何候选",
                Candidates = ranked,
                SearchMilliseconds = coarseMs,
                UsedFastStrategy = true,
                FastCoarseSearchMilliseconds = coarseMs,
                FastCoarseCandidateCount = candidates.Count,
                LockedScale = baselineScale,
                ReferenceWidth = reference.Edges.Width,
                ReferenceHeight = reference.Edges.Height,
            };
        }

        // Stage 3: 精修 + ECC
        var refineTimer = Stopwatch.StartNew();
        var refined = RefineCandidate(
            ranked[0],
            live,
            reference,
            referenceDistance,
            request,
            tuning);
        refineTimer.Stop();

        if (refined.CompositeCost > ranked[0].CompositeCost + 0.001d
            && !request.ForceBestCandidate)
        {
            return new MapStructureRegistrationResult
            {
                RejectionReason = MapStructureRejectionReason.RefinementFailed,
                FailureReason = MapStructureRejectionReason.RefinementFailed.ToDisplayText(),
                Candidates = ranked,
                SearchMilliseconds = coarseMs,
                RefineMilliseconds = refineTimer.Elapsed.TotalMilliseconds,
                UsedFastStrategy = true,
                FastCoarseSearchMilliseconds = coarseMs,
                FastCoarseCandidateCount = candidates.Count,
                LockedScale = baselineScale,
                ReferenceWidth = reference.Edges.Width,
                ReferenceHeight = reference.Edges.Height,
            };
        }

        var finalRanked = new[] { refined }
            .Concat(ranked.Skip(1))
            .OrderBy(candidate => candidate.CompositeCost)
            .ToArray();
        var best = finalRanked[0];
        var secondScore = finalRanked.Length > 1
            ? finalRanked[1].CompositeCost
            : double.PositiveInfinity;
        var margin = double.IsPositiveInfinity(secondScore)
            ? 1d
            : Math.Clamp(
                (secondScore - best.CompositeCost)
                / Math.Max(0.01d, secondScore),
                0d,
                1d);
        var requiredMargin = tuning.MinimumCandidateMargin
            * (best.UsedGlobalSearch ? 1.25d : 1d);
        var rejection = Validate(best, margin, requiredMargin, tuning);
        var confidenceBreakdown = MapStructureConfidenceCalculator.Calculate(
            best,
            margin,
            tuning,
            rejection,
            isTrackingMode: request.TrackingMode,
            sideEntrancePrior: request.SideEntrancePrior);
        var confidence = confidenceBreakdown.FinalScore;

        if (rejection != MapStructureRejectionReason.None
            && !request.ForceBestCandidate)
        {
            var rejectionDetails = CreateConfidenceLogDetails(
                confidenceBreakdown);
            rejectionDetails["fastCoarseMs"] = coarseMs;
            rejectionDetails["fastCandidates"] = candidates.Count;
            rejectionDetails["bestScore"] = best.CompositeCost;
            rejectionDetails["rejection"] = rejection.ToString();
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                $"快速粗搜索未通过验证：{rejection.ToDisplayText()}",
                elapsedMs: coarseMs + refineTimer.Elapsed.TotalMilliseconds,
                details: rejectionDetails);
            return new MapStructureRegistrationResult
            {
                RejectionReason = rejection,
                FailureReason = rejection.ToDisplayText(),
                Confidence = confidence,
                ConfidenceBreakdown = confidenceBreakdown,
                BestScore = best.CompositeCost,
                SecondScore = secondScore,
                CandidateMargin = margin,
                Candidates = finalRanked,
                SearchMilliseconds = coarseMs,
                RefineMilliseconds = refineTimer.Elapsed.TotalMilliseconds,
                UsedFastStrategy = true,
                FastCoarseSearchMilliseconds = coarseMs,
                FastCoarseCandidateCount = candidates.Count,
                LockedScale = baselineScale,
                ReferenceWidth = reference.Edges.Width,
                ReferenceHeight = reference.Edges.Height,
                EccConverged = best.EccConverged,
                EccCorrelation = best.EccCorrelation,
            };
        }

        // Stage 4: 构建变换
        var transform = BuildTransform(best, request, reference);
        var acceptedDetails = CreateConfidenceLogDetails(
            confidenceBreakdown);
        acceptedDetails["bestScore"] = best.CompositeCost;
        acceptedDetails["margin"] = margin;
        acceptedDetails["fastCoarseMs"] = coarseMs;
        acceptedDetails["fastCandidates"] = candidates.Count;
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            $"快速粗搜索通过 · 置信度 {confidence:P0} · 最佳分数 {best.CompositeCost:F3} · 粗搜索 {coarseMs:F1}ms",
            details: acceptedDetails);
        return new MapStructureRegistrationResult
        {
            Accepted = true,
            Transform = transform,
            Confidence = confidence,
            ConfidenceBreakdown = confidenceBreakdown,
            BestScore = best.CompositeCost,
            SecondScore = secondScore,
            CandidateMargin = margin,
            Candidates = finalRanked,
            SearchMilliseconds = coarseMs,
            RefineMilliseconds = refineTimer.Elapsed.TotalMilliseconds,
            UsedFastStrategy = true,
            FastCoarseSearchMilliseconds = coarseMs,
            FastCoarseCandidateCount = candidates.Count,
            LockedScale = baselineScale,
            ReferenceWidth = reference.Edges.Width,
            ReferenceHeight = reference.Edges.Height,
            EccConverged = best.EccConverged,
            EccCorrelation = best.EccCorrelation,
        };
    }

    /// <summary>
    /// 降采样粗搜索：将 query 缩放到 1/D，在降采样的参考 distance map 上
    /// 做 Chamfer 评分（MatchTemplate CCORR），NMS 提取 Top-K 峰值，
    /// 映射回全分辨率后用 Evaluate() 精确评分。
    /// </summary>
    private static void CollectFastCoarseCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> output)
    {
        var D = tuning.FastCoarseDownsampleFactor;
        using var fullTemplate = new Mat(query.Edges, query.Bounds);

        // 计算降采样后的目标尺寸
        var targetWidth = Math.Max(1, fullTemplate.Width / D);
        var targetHeight = Math.Max(1, fullTemplate.Height / D);
        var refTargetWidth = Math.Max(1, reference.Edges.Width / D);
        var refTargetHeight = Math.Max(1, reference.Edges.Height / D);

        // 使用 FastCoarseMaxDimension 限制粗搜索最大分辨率
        var maxDim = Math.Max(targetWidth, targetHeight);
        if (maxDim > tuning.FastCoarseMaxDimension)
        {
            var extraScale = (double)tuning.FastCoarseMaxDimension / maxDim;
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * extraScale));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * extraScale));
            refTargetWidth = Math.Max(1, (int)Math.Round(refTargetWidth * extraScale));
            refTargetHeight = Math.Max(1, (int)Math.Round(refTargetHeight * extraScale));
        }
        if (targetWidth < 12 || targetHeight < 12)
            return;

        // 降采样 query edges
        using var template = new Mat();
        Cv2.Resize(
            fullTemplate,
            template,
            new Size(targetWidth, targetHeight),
            interpolation: InterpolationFlags.Area);

        // 降采样 reference edges + DistanceTransform
        using var refEdgesDown = new Mat();
        Cv2.Resize(
            reference.Edges,
            refEdgesDown,
            new Size(refTargetWidth, refTargetHeight),
            interpolation: InterpolationFlags.Area);
        using var inverse = new Mat();
        Cv2.BitwiseNot(refEdgesDown, inverse);
        using var coarseDistMap = new Mat();
        Cv2.DistanceTransform(
            inverse,
            coarseDistMap,
            DistanceTypes.L2,
            DistanceTransformMasks.Mask3);

        // MatchTemplate: 在 distance map 上用 CCORR
        // 距离图在边缘处=0，远离边缘=大值
        // query 模板在边缘处=255，非边缘处=0
        // CCORR = sum(dist * template) → 好匹配 = 低分数
        using var templateFloat = new Mat();
        template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
        using var scores = new Mat();
        Cv2.MatchTemplate(
            coarseDistMap,
            templateFloat,
            scores,
            TemplateMatchModes.CCorr);
        // 用边缘像素数归一化
        var edgePixelCount = Math.Max(1, Cv2.CountNonZero(template));
        Cv2.Multiply(scores, 1d / edgePixelCount, scores);

        // 计算从降采样坐标到全分辨率坐标的映射
        var actualDownsampleX = (double)reference.Edges.Width / refTargetWidth;
        var actualDownsampleY = (double)reference.Edges.Height / refTargetHeight;

        // NMS 提取 Top-K 峰值
        var suppression = Math.Max(2, tuning.FastCoarseNmsRadius);
        for (var index = 0; index < tuning.FastCoarseTopK; index++)
        {
            Cv2.MinMaxLoc(
                scores,
                out var minimum,
                out _,
                out var location,
                out _);
            if (!double.IsFinite(minimum))
                break;

            var referenceX = (int)Math.Round(location.X * actualDownsampleX);
            var referenceY = (int)Math.Round(location.Y * actualDownsampleY);

            if (referenceX >= 0
                && referenceY >= 0
                && referenceX + query.Bounds.Width < reference.Edges.Width
                && referenceY + query.Bounds.Height < reference.Edges.Height)
            {
                output.Add(Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch: true,
                    tuning));
            }

            // 圆形抑制
            var left = Math.Max(0, location.X - suppression);
            var top = Math.Max(0, location.Y - suppression);
            var right = Math.Min(scores.Width, location.X + suppression + 1);
            var bottom = Math.Min(scores.Height, location.Y + suppression + 1);
            Cv2.Rectangle(
                scores,
                new Rect(left, top, right - left, bottom - top),
                Scalar.All(double.PositiveInfinity),
                -1);
        }
    }

    private static IReadOnlyList<double> BuildScaleHypotheses(
        double baseline,
        bool allowScaleSearch,
        double scaleSearchRadius,
        double scaleSearchStep)
    {
        if (!allowScaleSearch || scaleSearchRadius <= 0d)
            return [baseline];
        // Keep global recovery bounded even for the 0.15/0.30 fallback
        // radii.  Eleven evenly distributed hypotheses preserve the existing
        // search budget while actually covering the requested radius.
        const int maximumStepsPerSide = 5;
        var count = Math.Clamp(
            (int)Math.Ceiling(scaleSearchRadius / scaleSearchStep),
            1,
            maximumStepsPerSide);
        var effectiveStep = scaleSearchRadius / count;
        var hypotheses = Enumerable.Range(-count, (count * 2) + 1)
            .Select(index => baseline * (1d + (index * effectiveStep)))
            .Where(scale => scale > StructureRegistrationRules.MinimumUsableScale)
            .DistinctBy(scale => Math.Round(scale, 6))
            .OrderBy(scale => Math.Abs(scale - baseline))
            .ToArray();
        return hypotheses;
    }

    private static QueryGeometry CreateQuery(
        MapStructureFeatures live,
        Size liveSize,
        double scale,
        bool includeVisibleMask = false)
    {
        var target = new Size(
            Math.Max(1, (int)Math.Round(liveSize.Width / scale)),
            Math.Max(1, (int)Math.Round(liveSize.Height / scale)));
        var structure = new Mat();
        var edges = new Mat();
        Cv2.Resize(
            live.StructureMask,
            structure,
            target,
            0d,
            0d,
            InterpolationFlags.Nearest);
        Cv2.Resize(
            live.Edges,
            edges,
            target,
            0d,
            0d,
            InterpolationFlags.Nearest);

        // VisibleMask 同步变换（与 StructureMask 相同的 target size 和插值方法）
        Mat? visibleMask = null;
        if (includeVisibleMask
            && live.RawVisibleMask is not null
            && !live.RawVisibleMask.Empty())
        {
            visibleMask = new Mat();
            Cv2.Resize(
                live.RawVisibleMask,
                visibleMask,
                target,
                0d, 0d,
                InterpolationFlags.Nearest);
        }

        var points = FindNonZeroPoints(edges);
        var bounds = points.Length == 0
            ? new Rect()
            : Cv2.BoundingRect(points);
        var relativeEdgePoints = points
            .Select(point => new Point(
                point.X - bounds.X,
                point.Y - bounds.Y))
            .ToArray();
        return new QueryGeometry(
            scale,
            structure,
            edges,
            bounds,
            relativeEdgePoints,
            visibleMask: visibleMask);
    }

    private static Mat CreateDistanceMap(
        MapStructureFeatures reference,
        double clip) =>
        reference.GetOrCreateClippedReferenceDistanceMap(clip);

    private static Point ExpectedReferenceLocation(
        MapStructureRegistrationRequest request,
        double scale,
        Rect queryBounds) =>
        new(
            (int)Math.Round(
                (request.ViewportBounds.X
                    + (queryBounds.X * scale)
                    - request.LockedTransform.OffsetX) / scale),
            (int)Math.Round(
                (request.ViewportBounds.Y
                    + (queryBounds.Y * scale)
                    - request.LockedTransform.OffsetY) / scale));

    private static Rect CenteredSearchRect(
        Size size,
        int centerX,
        int centerY,
        int radius)
    {
        var left = Math.Clamp(centerX - radius, 0, Math.Max(0, size.Width - 1));
        var top = Math.Clamp(centerY - radius, 0, Math.Max(0, size.Height - 1));
        var right = Math.Clamp(centerX + radius + 1, left + 1, size.Width);
        var bottom = Math.Clamp(centerY + radius + 1, top + 1, size.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static void SearchRestrictedCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        Point expected,
        Rect searchDomain,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> output)
    {
        var current = Evaluate(
            query,
            reference,
            referenceDistance,
            request,
            scale,
            Math.Clamp(expected.X, searchDomain.X, searchDomain.Right - 1),
            Math.Clamp(expected.Y, searchDomain.Y, searchDomain.Bottom - 1),
            usedGlobalSearch: false,
            tuning);
        output.Add(current);
        if (IsStrongAbsoluteCandidate(current, tuning))
            return;

        using var template = new Mat(query.Edges, query.Bounds);
        using var templateFloat = new Mat();
        template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
        using var referencePatch = new Mat(
            referenceDistance,
            new Rect(
                searchDomain.X,
                searchDomain.Y,
                templateFloat.Width + searchDomain.Width - 1,
                templateFloat.Height + searchDomain.Height - 1));
        using var scores = new Mat();
        Cv2.MatchTemplate(
            referencePatch,
            templateFloat,
            scores,
            TemplateMatchModes.CCorr);
        Cv2.Multiply(
            scores,
            1d / Math.Max(1, query.EdgeCount),
            scores);
        CollectCandidates(
            scores,
            query,
            reference,
            referenceDistance,
            request,
            scale,
            new Rect(0, 0, scores.Width, scores.Height),
            usedGlobalSearch: false,
            tuning,
            output,
            searchDomain.X,
            searchDomain.Y);
    }

    private static void CollectHistoryCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> output)
    {
        foreach (var transform in request.CandidateHistory
            .Where(candidate => candidate?.IsValid is true)
            .TakeLast(5))
        {
            if (Math.Abs((transform.Scale / scale) - 1d)
                > StructureRegistrationRules.ScaleAgreementTolerance)
                continue;
            var referenceX = (int)Math.Round(
                (request.ViewportBounds.X
                    + (query.Bounds.X * scale)
                    - transform.TranslationX) / scale);
            var referenceY = (int)Math.Round(
                (request.ViewportBounds.Y
                    + (query.Bounds.Y * scale)
                    - transform.TranslationY) / scale);
            if (referenceX < 0
                || referenceY < 0
                || referenceX + query.Bounds.Width
                    >= reference.Edges.Width
                || referenceY + query.Bounds.Height
                    >= reference.Edges.Height)
            {
                continue;
            }
            output.Add(Evaluate(
                query,
                reference,
                referenceDistance,
                request,
                scale,
                referenceX,
                referenceY,
                usedGlobalSearch: false,
                tuning));
        }
    }

    private static bool IsStrongAbsoluteCandidate(
        MapStructureCandidate candidate,
        MapStructureRegistrationTuning tuning) =>
            candidate.ChamferPixels
                <= tuning.MaximumChamferPixels
                    * StructureRegistrationRules.StrictChamferFactor
        && candidate.EdgeCoverage
            >= tuning.MinimumEdgeCoverage
                + StructureRegistrationRules.StrictEdgeCoverageMargin
        && candidate.OccupancyCoverage
            >= tuning.MinimumOccupancyCoverage
                + StructureRegistrationRules.StrictOccupancyMargin
        && candidate.ConsistentPartitions >= Math.Max(
            3,
            tuning.MinimumConsistentPartitions);

    private static void CollectFeatureCandidates(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        QueryGeometry query,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> output,
        out int matchCount,
        out int maximumInliers)
    {
        matchCount = 0;
        maximumInliers = 0;
        if (live.Descriptors.Empty()
            || reference.Descriptors.Empty()
            || live.KeyPoints.Length == 0
            || reference.KeyPoints.Length == 0)
        {
            return;
        }

        try
        {
            using var matcher = new BFMatcher(NormTypes.Hamming);
            var groups = matcher.KnnMatch(
                live.Descriptors,
                reference.Descriptors,
                2);
            var votes = new List<TranslationVote>();
            foreach (var group in groups)
            {
                if (group.Length < 2
                    || group[0].Distance
                        >= group[1].Distance * tuning.FeatureRatioThreshold)
                {
                    continue;
                }
                var match = group[0];
                if (match.QueryIdx < 0
                    || match.QueryIdx >= live.KeyPoints.Length
                    || match.TrainIdx < 0
                    || match.TrainIdx >= reference.KeyPoints.Length)
                {
                    continue;
                }

                var livePoint = live.KeyPoints[match.QueryIdx].Pt;
                var referencePoint = reference.KeyPoints[match.TrainIdx].Pt;
                var repeatX = Math.Clamp(
                    (int)Math.Round(referencePoint.X),
                    0,
                    reference.RepeatedRegionMask.Width - 1);
                var repeatY = Math.Clamp(
                    (int)Math.Round(referencePoint.Y),
                    0,
                    reference.RepeatedRegionMask.Height - 1);
                if (!reference.RepeatedRegionMask.Empty()
                    && reference.RepeatedRegionMask.At<byte>(
                        repeatY,
                        repeatX) != 0)
                {
                    continue;
                }

                votes.Add(new TranslationVote(
                    request.ViewportBounds.X + livePoint.X
                        - (referencePoint.X * scale),
                    request.ViewportBounds.Y + livePoint.Y
                        - (referencePoint.Y * scale),
                    match.Distance));
            }

            matchCount = votes.Count;
            if (votes.Count < 3)
                return;
            var tolerance = Math.Max(
                2d,
                tuning.FeatureInlierTolerancePixels);
            var clusters = votes
                .Select(seed =>
                {
                    var inliers = votes
                        .Where(vote => Distance(
                                seed.OffsetX,
                                seed.OffsetY,
                                vote.OffsetX,
                                vote.OffsetY)
                            <= tolerance)
                        .ToArray();
                    var weight = inliers.Sum(vote =>
                        1d / Math.Max(1d, vote.DescriptorDistance));
                    return new TranslationCluster(
                        inliers.Sum(vote =>
                                vote.OffsetX
                                / Math.Max(1d, vote.DescriptorDistance))
                            / Math.Max(0.0001d, weight),
                        inliers.Sum(vote =>
                                vote.OffsetY
                                / Math.Max(1d, vote.DescriptorDistance))
                            / Math.Max(0.0001d, weight),
                        inliers.Length);
                })
                .OrderByDescending(cluster => cluster.InlierCount)
                .ThenBy(cluster => Distance(
                    cluster.OffsetX,
                    cluster.OffsetY,
                    request.LockedTransform.OffsetX,
                    request.LockedTransform.OffsetY))
                .DistinctBy(cluster => (
                    (int)Math.Round(cluster.OffsetX / tolerance),
                    (int)Math.Round(cluster.OffsetY / tolerance)))
                .Take(tuning.MaximumTranslationCandidates)
                .ToArray();

            foreach (var cluster in clusters)
            {
                maximumInliers = Math.Max(
                    maximumInliers,
                    cluster.InlierCount);
                if (request.RestrictSearchToLockedTransform
                    && Distance(
                        cluster.OffsetX,
                        cluster.OffsetY,
                        request.LockedTransform.OffsetX,
                        request.LockedTransform.OffsetY)
                        > tuning.PreviousAlignmentSearchRadiusPixels)
                {
                    continue;
                }
                var referenceX = (int)Math.Round(
                    (request.ViewportBounds.X
                        + (query.Bounds.X * scale)
                        - cluster.OffsetX) / scale);
                var referenceY = (int)Math.Round(
                    (request.ViewportBounds.Y
                        + (query.Bounds.Y * scale)
                        - cluster.OffsetY) / scale);
                if (referenceX < 0
                    || referenceY < 0
                    || referenceX + query.Bounds.Width
                        >= reference.Edges.Width
                    || referenceY + query.Bounds.Height
                        >= reference.Edges.Height)
                {
                    continue;
                }
                var evaluated = Evaluate(
                    query,
                    reference,
                    reference.GetOrCreateClippedReferenceDistanceMap(
                        tuning.DistanceClipPixels),
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch:
                        !request.RestrictSearchToLockedTransform,
                    tuning);
                var consensus = Math.Clamp(
                    cluster.InlierCount / (double)Math.Max(3, votes.Count),
                    0d,
                    1d);
                output.Add(evaluated with
                {
                    FeatureInlierCount = cluster.InlierCount,
                    FeatureConsensus = consensus,
                    CompositeCost = Math.Max(
                        0d,
                        evaluated.CompositeCost - (consensus * 0.5d))
                });
            }
        }
        catch (OpenCVException)
        {
            matchCount = 0;
            maximumInliers = 0;
        }
    }

    private static void CollectPyramidCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> output)
    {
        if (reference.EdgePyramid.Count < 3)
            return;
        using var fullTemplate = new Mat(query.Edges, query.Bounds);
        var targetWidth = Math.Max(1, fullTemplate.Width / 4);
        var targetHeight = Math.Max(1, fullTemplate.Height / 4);
        if (targetWidth >= reference.EdgePyramid[2].Width
            || targetHeight >= reference.EdgePyramid[2].Height)
        {
            return;
        }

        using var template = new Mat();
        Cv2.Resize(
            fullTemplate,
            template,
            new Size(targetWidth, targetHeight),
            interpolation: InterpolationFlags.Area);
        using var inverse = new Mat();
        using var distanceMap = new Mat();
        Cv2.BitwiseNot(reference.EdgePyramid[2], inverse);
        Cv2.DistanceTransform(
            inverse,
            distanceMap,
            DistanceTypes.L2,
            DistanceTransformMasks.Mask3);
        using var templateFloat = new Mat();
        template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
        using var scores = new Mat();
        Cv2.MatchTemplate(
            distanceMap,
            templateFloat,
            scores,
            TemplateMatchModes.CCorr);
        Cv2.Multiply(
            scores,
            1d / Math.Max(1, Cv2.CountNonZero(template)),
            scores);
        var suppression = Math.Max(
            3,
            Math.Min(template.Width, template.Height) / 3);
        for (var index = 0;
             index < tuning.MaximumTranslationCandidates;
             index++)
        {
            Cv2.MinMaxLoc(
                scores,
                out var minimum,
                out _,
                out var location,
                out _);
            if (!double.IsFinite(minimum))
                break;
            var referenceX = location.X * 4;
            var referenceY = location.Y * 4;
            if (referenceX + query.Bounds.Width < reference.Edges.Width
                && referenceY + query.Bounds.Height < reference.Edges.Height)
            {
                output.Add(Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch: true,
                    tuning));
            }
            var left = Math.Max(0, location.X - suppression);
            var top = Math.Max(0, location.Y - suppression);
            var right = Math.Min(
                scores.Width,
                location.X + suppression + 1);
            var bottom = Math.Min(
                scores.Height,
                location.Y + suppression + 1);
            Cv2.Rectangle(
                scores,
                new Rect(left, top, right - left, bottom - top),
                Scalar.All(double.PositiveInfinity),
                -1);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Visible-aware 快速候选生成（两次 MatchTemplate → IoU 响应图）
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct VisibleAwareSearchDiagnostics(
        bool Ran,
        double SearchMilliseconds,
        int CandidateCount,
        double BestCost,
        double SecondCost,
        double VisibleFraction,
        int VisibleStructurePixels,
        int VisibleEdgePixels)
    {
        public static readonly VisibleAwareSearchDiagnostics Empty = new();
    }

    private static VisibleAwareSearchDiagnostics CollectVisibleAwareCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> candidates)
    {
        // 门控：开关未启用 → 立即返回
        if (!tuning.EnableVisibleAwareShadow
            && !tuning.EnableVisibleAwareInjection)
            return VisibleAwareSearchDiagnostics.Empty;

        // 无 VisibleMask → 返回
        if (query.VisibleMask is null || query.VisibleMask.Empty())
            return VisibleAwareSearchDiagnostics.Empty;

        // 可见像素不足 → 返回
        var totalVisible = Cv2.CountNonZero(query.VisibleMask);
        var visibleFraction = (double)totalVisible
            / (query.VisibleMask.Width * query.VisibleMask.Height);
        if (visibleFraction < tuning.VisibleAwareMinimumVisibleFraction)
            return VisibleAwareSearchDiagnostics.Empty;

        // 裁剪到 query.Bounds
        using var visibleCropped = new Mat(query.VisibleMask, query.Bounds);

        // 腐蚀得到 SafeVisibleMask
        var erodeSize = 1 + tuning.SafeVisibleMaskErodePixels * 2;
        using var erodeKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, new Size(erodeSize, erodeSize));
        using var safeVisible = new Mat();
        Cv2.Erode(visibleCropped, safeVisible, erodeKernel);

        // 派生 VisibleStructure 和 VisibleEdges
        // Crop structure and edges to query.Bounds first so they match
        // safeVisible dimensions. BitwiseAnd requires same-size inputs.
        using var structureCropped = new Mat(query.Structure, query.Bounds);
        using var edgesCropped = new Mat(query.Edges, query.Bounds);
        using var visibleStructure = new Mat();
        Cv2.BitwiseAnd(structureCropped, safeVisible, visibleStructure);

        var visibleStructurePixels = Cv2.CountNonZero(visibleStructure);
        if (visibleStructurePixels < tuning.VisibleAwareMinimumVisibleStructurePixels)
            return VisibleAwareSearchDiagnostics.Empty;

        using var visibleEdges = new Mat();
        Cv2.BitwiseAnd(edgesCropped, safeVisible, visibleEdges);

        // ═══════════════════════════════════════════════════════
        // 两次相关运算生成 IoU 响应图
        // ═══════════════════════════════════════════════════════

        // TP(x) = sum(refStructure * visibleStructure) 对于每个位置 x
        using var tpResponse = new Mat();
        Cv2.MatchTemplate(
            reference.StructureMask,
            visibleStructure,
            tpResponse,
            TemplateMatchModes.CCorr);

        // RefVisibleStructure(x) = sum(refStructure * safeVisible) 对于每个位置 x
        using var refVisStructResponse = new Mat();
        Cv2.MatchTemplate(
            reference.StructureMask,
            safeVisible,
            refVisStructResponse,
            TemplateMatchModes.CCorr);

        // IoU(x) = TP(x) / (LiveStructureCount + RefVisibleStructure(x) - TP(x))
        var liveStructCount = (double)visibleStructurePixels;

        using var tpFloat = new Mat();
        tpResponse.ConvertTo(tpFloat, MatType.CV_32FC1);

        using var refVisFloat = new Mat();
        refVisStructResponse.ConvertTo(refVisFloat, MatType.CV_32FC1);

        using var union = new Mat();
        Cv2.Add(refVisFloat, liveStructCount, union);
        Cv2.Subtract(union, tpFloat, union);
        Cv2.Max(union, 1d, union);  // 避免除零

        using var iouResponse = new Mat();
        Cv2.Divide(tpFloat, union, iouResponse);

        // ═══════════════════════════════════════════════════════
        // Top-K 提取 + NMS
        // ═══════════════════════════════════════════════════════

        var suppressRadius = Math.Max(
            4,
            Math.Min(iouResponse.Width, iouResponse.Height) / 8);
        var rawCandidates = new List<(int X, int Y, double IoU)>();
        var maxK = tuning.VisibleAwareTopK * 3;

        var nmsScores = iouResponse.Clone();
        for (int k = 0; k < maxK; k++)
        {
            Cv2.MinMaxLoc(nmsScores, out _, out var maxVal,
                out _, out var maxLoc);
            if (maxVal <= 0d)
                break;

            rawCandidates.Add((maxLoc.X, maxLoc.Y, maxVal));

            Cv2.Circle(nmsScores, maxLoc, suppressRadius,
                Scalar.All(0d), -1);

            if (rawCandidates.Count >= tuning.VisibleAwareTopK)
                break;
        }
        nmsScores.Dispose();

        // ═══════════════════════════════════════════════════════
        // 映射回完整分辨率 + 通过 Evaluate() 精确评估
        // ═══════════════════════════════════════════════════════

        var visibleEdgePixels = Cv2.CountNonZero(visibleEdges);
        var evaluatedCosts = new List<double>(rawCandidates.Count);

        foreach (var (x, y, iouScore) in rawCandidates)
        {
            // MatchTemplate returns the position of the cropped template
            // (already offset by query.Bounds) in the reference image.
            // Do NOT add query.Bounds.X/Y again — that would double the
            // internal crop offset.
            var refX = x;
            var refY = y;

            // 通过现有 Evaluate() 获取完整可比较评分
            var evaluated = Evaluate(
                query, reference, referenceDistance, request, scale,
                refX, refY,
                usedGlobalSearch: true, tuning);

            evaluatedCosts.Add(evaluated.CompositeCost);

            var candidate = evaluated with
            {
                FromVisibleAware = true,
                VisibleFraction = visibleFraction,
                VisibleStructurePixels = visibleStructurePixels,
                VisibleEdgePixels = visibleEdgePixels
            };

            if (tuning.EnableVisibleAwareInjection)
                candidates.Add(candidate);
        }

        evaluatedCosts.Sort();
        return new VisibleAwareSearchDiagnostics(
            Ran: true,
            SearchMilliseconds: 0d,
            CandidateCount: rawCandidates.Count,
            BestCost: evaluatedCosts.Count > 0
                ? evaluatedCosts[0] : double.PositiveInfinity,
            SecondCost: evaluatedCosts.Count > 1
                ? evaluatedCosts[1] : double.PositiveInfinity,
            VisibleFraction: visibleFraction,
            VisibleStructurePixels: visibleStructurePixels,
            VisibleEdgePixels: visibleEdgePixels);
    }

    private static void CollectCandidates(
        Mat scores,
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        Rect searchRect,
        bool usedGlobalSearch,
        MapStructureRegistrationTuning tuning,
        List<MapStructureCandidate> output,
        int originX = 0,
        int originY = 0)
    {
        if (searchRect.Width <= 0 || searchRect.Height <= 0)
            return;
        using var search = new Mat(scores, searchRect).Clone();
        var suppressionRadius = Math.Max(
            tuning.MinimumSpanPixels,
            Math.Min(query.Bounds.Width, query.Bounds.Height) / 3);
        for (var index = 0; index < tuning.TopCandidateCount; index++)
        {
            Cv2.MinMaxLoc(search, out var score, out _, out var location, out _);
            if (!double.IsFinite(score))
                break;
            var referenceX = originX + searchRect.X + location.X;
            var referenceY = originY + searchRect.Y + location.Y;
            // Only skip the same integer location here. Nearby points can
            // still be materially better and are deduplicated after their
            // full structural score is known.
            const double duplicateRadius = 1d;
            if (!output.Any(candidate =>
                    Math.Abs(candidate.Scale - scale) < 0.000001d
                    && Math.Sqrt(
                        Math.Pow(candidate.ReferenceX - referenceX, 2d)
                        + Math.Pow(candidate.ReferenceY - referenceY, 2d))
                        < duplicateRadius))
            {
                output.Add(Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch,
                    tuning));
            }
            var left = Math.Max(0, location.X - suppressionRadius);
            var top = Math.Max(0, location.Y - suppressionRadius);
            var right = Math.Min(search.Width, location.X + suppressionRadius + 1);
            var bottom = Math.Min(search.Height, location.Y + suppressionRadius + 1);
            Cv2.Rectangle(
                search,
                new Rect(left, top, right - left, bottom - top),
                Scalar.All(double.PositiveInfinity),
                -1);
        }
    }

    private static MapStructureCandidate Evaluate(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        int referenceX,
        int referenceY,
        bool usedGlobalSearch,
        MapStructureRegistrationTuning tuning)
    {
        using var queryEdges = new Mat(query.Edges, query.Bounds);
        using var queryStructure = new Mat(query.Structure, query.Bounds);
        using var distancePatch = new Mat(
            referenceDistance,
            new Rect(
                referenceX,
                referenceY,
                query.Bounds.Width,
                query.Bounds.Height));
        using var referenceStructurePatch = new Mat(
            reference.StructureMask,
            new Rect(
                referenceX,
                referenceY,
                query.Bounds.Width,
                query.Bounds.Height));
        var chamfer = Cv2.Mean(distancePatch, queryEdges).Val0;
        using var withinTolerance = new Mat();
        using var coveredEdges = new Mat();
        Cv2.Compare(
            distancePatch,
            tuning.EdgeDistanceTolerancePixels,
            withinTolerance,
            CmpTypes.LE);
        Cv2.BitwiseAnd(withinTolerance, queryEdges, coveredEdges);
        var covered = Cv2.CountNonZero(coveredEdges);
        var edgeCoverage = covered / (double)Math.Max(1, query.EdgeCount);

        using var occupancyOverlap = new Mat();
        Cv2.BitwiseAnd(
            referenceStructurePatch,
            queryStructure,
            occupancyOverlap);
        var occupancyCoverage = Cv2.CountNonZero(occupancyOverlap)
            / (double)Math.Max(1, Cv2.CountNonZero(queryStructure));

        var partitionCounts = new int[4];
        var partitionCovered = new int[4];
        var halfWidth = query.Bounds.Width / 2;
        var halfHeight = query.Bounds.Height / 2;
        var partitions = new[]
        {
            new Rect(0, 0, halfWidth, halfHeight),
            new Rect(
                halfWidth,
                0,
                query.Bounds.Width - halfWidth,
                halfHeight),
            new Rect(
                0,
                halfHeight,
                halfWidth,
                query.Bounds.Height - halfHeight),
            new Rect(
                halfWidth,
                halfHeight,
                query.Bounds.Width - halfWidth,
                query.Bounds.Height - halfHeight)
        };
        for (var index = 0; index < partitions.Length; index++)
        {
            using var partitionEdges = new Mat(queryEdges, partitions[index]);
            using var partitionOverlap = new Mat(
                coveredEdges,
                partitions[index]);
            partitionCounts[index] = Cv2.CountNonZero(partitionEdges);
            partitionCovered[index] = Cv2.CountNonZero(partitionOverlap);
        }
        var consistentPartitions = Enumerable.Range(0, 4)
            .Count(index => partitionCounts[index] >= 12
                && partitionCovered[index] / (double)partitionCounts[index] >= 0.45d);
        var composite = chamfer
            + ((1d - edgeCoverage) * 4d)
            + ((1d - occupancyCoverage) * 2d)
            + (Math.Max(
                0,
                tuning.MinimumConsistentPartitions - consistentPartitions)
                * StructureRegistrationRules.PartitionPenaltyWeight);
        var offsetX = request.ViewportBounds.X
            + (query.Bounds.X * scale)
            - (referenceX * scale);
        var offsetY = request.ViewportBounds.Y
            + (query.Bounds.Y * scale)
            - (referenceY * scale);
        var bounds = request.ValidMapBounds?.IsValid is true
            ? request.ValidMapBounds
            : MapReferenceBounds.FullImage(
                reference.Edges.Width,
                reference.Edges.Height);
        var viewportOrigin = new MapViewportOrigin(
            (request.ViewportBounds.X - offsetX) / scale,
            (request.ViewportBounds.Y - offsetY) / scale);
        // ViewportBounds is the user-calibrated native map canvas, not a crop
        // that must fit inside the reference image. Small maps can be fully
        // surrounded by empty canvas, so validating the entire viewport would
        // reject every correct transform whenever viewport / scale is larger
        // than the reference. Validate the structure that actually supported
        // this candidate instead.
        var boundsTolerance = 2d / scale;
        var isWithinBounds =
            referenceX >= bounds.X - boundsTolerance
            && referenceY >= bounds.Y - boundsTolerance
            && referenceX + query.Bounds.Width
                <= bounds.Right + boundsTolerance
            && referenceY + query.Bounds.Height
                <= bounds.Bottom + boundsTolerance;
        var predicted = request.PredictedViewportOrigin;
        if (predicted is null
            && request.PlayerPrior is { } playerPrior)
        {
            predicted = MapSessionRules.PredictViewportOrigin(
                playerPrior,
                request.ViewportBounds.Width,
                request.ViewportBounds.Height,
                scale,
                bounds);
        }
        var priorAgreement = 1d;
        if (predicted is { } expectedOrigin)
        {
            var diagonal = Math.Sqrt(
                (bounds.Width * bounds.Width)
                + (bounds.Height * bounds.Height));
            priorAgreement = 1d - Math.Clamp(
                Distance(
                    viewportOrigin.X,
                    viewportOrigin.Y,
                    expectedOrigin.X,
                    expectedOrigin.Y)
                / Math.Max(
                    1d,
                    diagonal * tuning.MaximumPlayerPriorDistanceRatio),
                0d,
                1d);
        }
        if (!isWithinBounds)
            composite += 100d;
        composite += (1d - priorAgreement)
            * StructureRegistrationRules.PriorDisagreementPenaltyWeight;
        return new MapStructureCandidate
        {
            Scale = scale,
            ReferenceX = referenceX,
            ReferenceY = referenceY,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ChamferPixels = chamfer,
            EdgeCoverage = edgeCoverage,
            OccupancyCoverage = occupancyCoverage,
            ConsistentPartitions = consistentPartitions,
            UsedGlobalSearch = usedGlobalSearch,
            CompositeCost = composite,
            PriorAgreement = priorAgreement,
            IsWithinValidBounds = isWithinBounds
        };
    }

    private static Point[] FindNonZeroPoints(Mat binary)
    {
        using var pointMatrix = new Mat();
        Cv2.FindNonZero(binary, pointMatrix);
        if (pointMatrix.Empty())
            return [];
        pointMatrix.GetArray(out Point[] points);
        return points;
    }

    private static IReadOnlyList<MapStructureCandidate> DistinctCandidates(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapOverlayTransform lockedTransform)
    {
        var distinct = new List<MapStructureCandidate>();
        foreach (var candidate in candidates
            .OrderBy(item => item.CompositeCost)
            .ThenBy(item => Distance(
                item.OffsetX,
                item.OffsetY,
                lockedTransform.OffsetX,
                lockedTransform.OffsetY)))
        {
            var duplicate = distinct.Any(existing =>
                StructureRegistrationRules.IsSameAlignmentBasin(
                    existing,
                    candidate,
                    tuning));
            if (!duplicate)
                distinct.Add(candidate);
        }
        return distinct;
    }

    private static MapStructureCandidate RefineCandidate(
        MapStructureCandidate candidate,
        MapStructureFeatures live,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning)
    {
        if (candidate.CompositeCost <= 0.001d)
            return candidate;
        using var query = CreateQuery(live, request.LiveRoi.Size(), candidate.Scale);
        var best = candidate;
        // Translation-only coarse-to-fine refinement. The search never
        // introduces scale, rotation, affine, or perspective freedom.
        foreach (var step in new[] { 8, 4, 2, 1 })
        {
            var center = best;
            foreach (var (deltaX, deltaY) in new[]
                     {
                         (-step, -step), (0, -step), (step, -step),
                         (-step, 0), (step, 0),
                         (-step, step), (0, step), (step, step)
                     })
            {
                var x = center.ReferenceX + deltaX;
                var y = center.ReferenceY + deltaY;
                if (x < 0
                    || y < 0
                    || x + query.Bounds.Width >= reference.Edges.Width
                    || y + query.Bounds.Height >= reference.Edges.Height)
                {
                    continue;
                }
                var evaluated = Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    candidate.Scale,
                    x,
                    y,
                    candidate.UsedGlobalSearch,
                    tuning) with
                {
                    FeatureInlierCount = candidate.FeatureInlierCount,
                    FeatureConsensus = candidate.FeatureConsensus
                };
                if (evaluated.CompositeCost < best.CompositeCost)
                    best = evaluated;
            }
        }
        if (!tuning.EnableEccRefinement)
            return best;
        if (best.CompositeCost <= tuning.SkipEccScoreThreshold)
            return best;
        return RefineTranslationWithEcc(best, query, reference);
    }

    private static bool CanSkipLocalRefinement(
        IReadOnlyList<MapStructureCandidate> ranked,
        MapStructureRegistrationTuning tuning)
    {
        if (tuning.EnableEccRefinement || ranked.Count == 0)
            return false;
        var best = ranked[0];
        var secondScore = ranked.Count > 1
            ? ranked[1].CompositeCost
            : double.PositiveInfinity;
        var margin = double.IsPositiveInfinity(secondScore)
            ? 1d
            : Math.Clamp(
                (secondScore - best.CompositeCost)
                / Math.Max(0.01d, secondScore),
                0d,
                1d);
        var requiredMargin = tuning.MinimumCandidateMargin
            * (best.UsedGlobalSearch ? 1.25d : 1d);
        return Validate(best, margin, requiredMargin, tuning)
                == MapStructureRejectionReason.None
            && best.ChamferPixels
                <= tuning.MaximumChamferPixels
                    * StructureRegistrationRules.StrictChamferFactor
            && best.EdgeCoverage
                >= tuning.MinimumEdgeCoverage
                    + StructureRegistrationRules.StrictOccupancyMargin
            && best.OccupancyCoverage
                >= tuning.MinimumOccupancyCoverage
                    + StructureRegistrationRules.StrictOccupancyMargin
            && best.ConsistentPartitions >= Math.Max(
                3,
                tuning.MinimumConsistentPartitions)
            && margin >= Math.Max(
                StructureRegistrationRules.MinimumReplacementMargin,
                requiredMargin * 2d);
    }

    private static MapStructureCandidate RefineTranslationWithEcc(
        MapStructureCandidate candidate,
        QueryGeometry query,
        MapStructureFeatures reference)
    {
        if (candidate.ReferenceX < 0
            || candidate.ReferenceY < 0
            || candidate.ReferenceX + query.Bounds.Width > reference.Edges.Width
            || candidate.ReferenceY + query.Bounds.Height > reference.Edges.Height)
        {
            return candidate;
        }
        try
        {
            using var referencePatch = new Mat(
                reference.Edges,
                new Rect(
                    candidate.ReferenceX,
                    candidate.ReferenceY,
                    query.Bounds.Width,
                    query.Bounds.Height));
            using var queryPatch = new Mat(query.Edges, query.Bounds);
            using var mask = new Mat(query.Structure, query.Bounds);
            using var referenceFloat = new Mat();
            using var queryFloat = new Mat();
            referencePatch.ConvertTo(referenceFloat, MatType.CV_32FC1, 1d / 255d);
            queryPatch.ConvertTo(queryFloat, MatType.CV_32FC1, 1d / 255d);
            using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
            var correlation = Cv2.FindTransformECC(
                referenceFloat,
                queryFloat,
                warp,
                MotionTypes.Translation,
                new TermCriteria(
                    CriteriaTypes.Count | CriteriaTypes.Eps,
                    30,
                    0.0001d),
                mask,
                3);
            var translationX = warp.At<float>(0, 2);
            var translationY = warp.At<float>(1, 2);
            if (!double.IsFinite(correlation)
                || correlation < StructureRegistrationRules.EccMinimumCorrelation
                || !float.IsFinite(translationX)
                || !float.IsFinite(translationY)
                || Math.Abs(translationX) > 2.5f
                || Math.Abs(translationY) > 2.5f)
            {
                return candidate;
            }

            // findTransformECC returns the warp from template coordinates to
            // input coordinates (OpenCV aligns with WARP_INVERSE_MAP). For
            // screen = scale * reference + offset, that warp translation is
            // added to the screen offset.
            return candidate with
            {
                OffsetX = candidate.OffsetX
                    + (translationX * candidate.Scale),
                OffsetY = candidate.OffsetY
                    + (translationY * candidate.Scale),
                EccConverged = true,
                EccCorrelation = correlation
            };
        }
        catch (OpenCVException)
        {
            return candidate;
        }
    }

    private static MapStructureRejectionReason Validate(
        MapStructureCandidate best,
        double margin,
        double requiredMargin,
        MapStructureRegistrationTuning tuning)
    {
        if (!best.IsWithinValidBounds)
            return MapStructureRejectionReason.OutsideValidBounds;
        if (best.PriorAgreement <= StructureRegistrationRules.MinimumPriorAgreement)
            return MapStructureRejectionReason.PlayerPriorMismatch;
        if (best.ChamferPixels > tuning.MaximumChamferPixels
            || best.EdgeCoverage < tuning.MinimumEdgeCoverage)
        {
            return MapStructureRejectionReason.WeakAbsoluteScore;
        }
        if (margin < requiredMargin)
            return MapStructureRejectionReason.AmbiguousCandidates;
        return MapStructureRejectionReason.None;
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

    /// <summary>
    /// Phase 5: 提前终止安全检查。条件比 <see cref="CanSkipLocalRefinement"/>
    /// 更严格，因为跳过 Legacy 候选生成比跳过 ECC 精修风险更高。
    /// </summary>
    /// <param name="best">Visible-aware 最佳候选（已通过 CompositeCost 阈值）</param>
    /// <param name="secondBestCost">Visible-aware 次佳候选的 CompositeCost（无穷大 = 仅有一个候选）</param>
    /// <param name="tuning">调优参数</param>
    /// <returns>所有条件通过返回 true</returns>
    private static bool MeetsEarlyTerminationCriteria(
        MapStructureCandidate best,
        double secondBestCost,
        MapStructureRegistrationTuning tuning)
    {
        // 必须在有效地图范围内
        if (!best.IsWithinValidBounds)
            return false;

        // PlayerPrior 不严重冲突（比 Validate 的 0.05 更严格）
        if (best.PriorAgreement <= StructureRegistrationRules.StrictPriorAgreement)
            return false;

        // Chamfer 阈值：比基础阈值严格 15%
        if (best.ChamferPixels
            > tuning.MaximumChamferPixels
                * StructureRegistrationRules.RefinementChamferFactor)
            return false;

        // EdgeCoverage：比基础阈值严格 0.10
        if (best.EdgeCoverage
            < tuning.MinimumEdgeCoverage
                + StructureRegistrationRules.RefinementEdgeCoverageMargin)
            return false;

        // OccupancyCoverage：比基础阈值严格 0.10
        if (best.OccupancyCoverage
            < tuning.MinimumOccupancyCoverage
                + StructureRegistrationRules.RefinementOccupancyMargin)
            return false;

        // PartitionConsistency：比基础阈值多要求 1 个分区
        if (best.ConsistentPartitions
            < Math.Max(4, tuning.MinimumConsistentPartitions + 1))
            return false;

        // Top-1 vs Top-2 边际
        if (double.IsFinite(secondBestCost))
        {
            var margin = (secondBestCost - best.CompositeCost)
                / Math.Max(0.01d, secondBestCost);
            if (margin < tuning.MinimumCandidateMargin * 1.5d)
                return false;
        }

        return true;
    }

    private static MapOverlayTransform BuildTransform(
        MapStructureCandidate candidate,
        MapStructureRegistrationRequest request,
        MapStructureFeatures reference)
    {
        var referenceCenterX = reference.Edges.Width / 2d;
        var referenceCenterY = reference.Edges.Height / 2d;
        return new MapOverlayTransform
        {
            ScaleX = candidate.Scale,
            ScaleY = candidate.Scale,
            OffsetX = candidate.OffsetX,
            OffsetY = candidate.OffsetY,
            ReferenceCenterX = referenceCenterX,
            ReferenceCenterY = referenceCenterY,
            ScreenCenterX = (referenceCenterX * candidate.Scale) + candidate.OffsetX,
            ScreenCenterY = (referenceCenterY * candidate.Scale) + candidate.OffsetY,
            ReferenceWidth = reference.Edges.Width,
            ReferenceHeight = reference.Edges.Height,
            OrientationDegrees = 0,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = candidate.ChamferPixels * candidate.Scale
        };
    }

    private static string ResolveDebugDirectory(string? requested)
    {
        var directory = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(
                global::IDVBuff.AppDataPaths.RootDirectory,
                "MapAlignmentDebug",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))
            : Path.GetFullPath(requested);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WritePreprocessDebug(
        string? directory,
        Mat liveRoi,
        MapStructureFeatures live,
        MapStructureFeatures reference)
    {
        if (directory is null)
            return;
        TryWrite(Path.Combine(directory, "01-roi.png"), liveRoi);
        TryWrite(Path.Combine(directory, "02-dynamic-mask.png"), live.NuisanceMask);
        TryWrite(Path.Combine(directory, "03-structure-mask.png"), live.StructureMask);
        TryWrite(Path.Combine(directory, "04-edges.png"), live.Edges);
        TryWrite(
            Path.Combine(directory, "05-reference-structure.png"),
            reference.StructureMask);
    }

    private static void WriteSearchDebug(
        string? directory,
        MapStructureFeatures reference,
        Mat? heatmap,
        QueryGeometry? query,
        IReadOnlyList<MapStructureCandidate> candidates)
    {
        if (directory is null)
            return;
        if (heatmap is not null && !heatmap.Empty())
        {
            using var normalizedFloat = new Mat();
            using var normalized = new Mat();
            Cv2.Normalize(
                heatmap,
                normalizedFloat,
                255d,
                0d,
                NormTypes.MinMax);
            normalizedFloat.ConvertTo(normalized, MatType.CV_8UC1);
            TryWrite(Path.Combine(directory, "06-search-heatmap.png"), normalized);
        }
        using var visual = new Mat();
        Cv2.CvtColor(reference.StructureMask, visual, ColorConversionCodes.GRAY2BGR);
        if (query is not null)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                Cv2.Rectangle(
                    visual,
                    new Rect(
                        candidate.ReferenceX,
                        candidate.ReferenceY,
                        Math.Min(query.Bounds.Width, visual.Width - candidate.ReferenceX),
                        Math.Min(query.Bounds.Height, visual.Height - candidate.ReferenceY)),
                    index == 0 ? Scalar.LimeGreen : Scalar.OrangeRed,
                    index == 0 ? 3 : 1);
            }
        }
        TryWrite(Path.Combine(directory, "07-top-candidates.png"), visual);
    }

    private static void WriteFinalDebug(
        string? directory,
        MapStructureRegistrationRequest request,
        MapStructureFeatures reference,
        MapStructureFeatures live,
        MapOverlayTransform transform)
    {
        if (directory is null)
            return;
        using var projected = new Mat();
        using var matrix = Mat.Zeros(2, 3, MatType.CV_64FC1).ToMat();
        matrix.Set(0, 0, transform.ScaleX);
        matrix.Set(0, 2, transform.OffsetX - request.ViewportBounds.X);
        matrix.Set(1, 1, transform.ScaleY);
        matrix.Set(1, 2, transform.OffsetY - request.ViewportBounds.Y);
        Cv2.WarpAffine(
            reference.Edges,
            projected,
            matrix,
            request.LiveRoi.Size(),
            InterpolationFlags.Nearest,
            BorderTypes.Constant,
            Scalar.Black);
        using var visual = new Mat(
            request.LiveRoi.Size(),
            MatType.CV_8UC3,
            Scalar.Black);
        visual.SetTo(new Scalar(0, 170, 0), live.Edges);
        visual.SetTo(new Scalar(0, 0, 220), projected);
        using var overlap = new Mat();
        Cv2.BitwiseAnd(live.Edges, projected, overlap);
        visual.SetTo(new Scalar(0, 255, 255), overlap);
        TryWrite(Path.Combine(directory, "08-final-overlay.png"), visual);
    }

    private static void TryWrite(string path, Mat image)
    {
        try
        {
            Cv2.ImWrite(path, image);
        }
        catch
        {
            // Debug output must never decide whether a transform is accepted.
        }
    }

    private static double Distance(
        double firstX,
        double firstY,
        double secondX,
        double secondY) =>
        Math.Sqrt(
            Math.Pow(firstX - secondX, 2d)
            + Math.Pow(firstY - secondY, 2d));

    private sealed record TranslationVote(
        double OffsetX,
        double OffsetY,
        double DescriptorDistance);

    private sealed record TranslationCluster(
        double OffsetX,
        double OffsetY,
        int InlierCount);

    private sealed class QueryGeometry : IDisposable
    {
        public QueryGeometry(
            double scale,
            Mat structure,
            Mat edges,
            Rect bounds,
            Point[] edgePoints,
            Mat? visibleMask = null)
        {
            Scale = scale;
            Structure = structure;
            Edges = edges;
            Bounds = bounds;
            EdgePoints = edgePoints;
            VisibleMask = visibleMask;
        }

        public double Scale { get; }
        public Mat Structure { get; }
        public Mat Edges { get; }
        public Rect Bounds { get; }
        public Point[] EdgePoints { get; }
        public int EdgeCount => EdgePoints.Length;
        public Mat? VisibleMask { get; }

        public QueryGeometry CloneForDebug() => new(
            Scale,
            Structure.Clone(),
            Edges.Clone(),
            Bounds,
            EdgePoints,
            VisibleMask?.Clone());

        public void Dispose()
        {
            Structure.Dispose();
            Edges.Dispose();
            VisibleMask?.Dispose();
        }
    }
}
