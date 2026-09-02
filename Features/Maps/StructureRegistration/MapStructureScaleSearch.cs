using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;
namespace IDVBuff.Features.Maps;
internal static partial class MapStructureScaleSearch
{
    internal static IReadOnlyList<double> BuildLowStructureScaleHypotheses(
        double minimumScale,
        double maximumScale,
        int count,
        double minimumUsableScale = 0.05d,
        double? preferredScale = null)
    {
        minimumScale = Math.Max(minimumUsableScale, minimumScale);
        maximumScale = Math.Max(minimumScale, maximumScale);
        count = Math.Max(2, count);
        if (count == 2)
            return [minimumScale, maximumScale];
        var logMinimum = Math.Log(minimumScale);
        var logMaximum = Math.Log(maximumScale);
        var scales = Enumerable.Range(0, count)
            .Select(index => Math.Exp(
                logMinimum + ((logMaximum - logMinimum) * index / (count - 1d))))
            .ToArray();
        if (preferredScale is { } preferred
            && double.IsFinite(preferred)
            && preferred >= minimumScale
            && preferred <= maximumScale
            && !scales.Any(scale => Math.Abs(scale - preferred) < 1e-9d))
        {
            var nearestInteriorIndex = Enumerable.Range(1, count - 2)
                .MinBy(index => Math.Abs(scales[index] - preferred));
            scales[nearestInteriorIndex] = preferred;
        }
        if (preferredScale is { } preferredOrder
            && double.IsFinite(preferredOrder)
            && preferredOrder >= minimumScale
            && preferredOrder <= maximumScale)
        {
            return scales
                .OrderBy(scale => Math.Abs(Math.Log(scale / preferredOrder)))
                .ToArray();
        }
        return scales;
    }
    internal static IReadOnlyList<double> BuildScaleHypotheses(
        double baseline,
        bool allowScaleSearch,
        double scaleSearchRadius,
        double scaleSearchStep)
    {
        if (!allowScaleSearch || scaleSearchRadius <= 0d)
            return [baseline];
        // The uncalibrated 0.30-1.70 recovery domain needs seven 0.10 steps
        // per side. Capping this at five made the wider domain deceptively
        // sparse and skipped important scales such as 0.40.
        const int maximumStepsPerSide = 7;
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
    internal static QueryGeometry CreateQuery(
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
        Mat? visibleMask = null;
        Mat? appearance = null;
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
        if (includeVisibleMask && !live.NormalizedGray.Empty())
        {
            appearance = new Mat();
            Cv2.Resize(
                live.NormalizedGray,
                appearance,
                target,
                interpolation: InterpolationFlags.Area);
        }
        var bounds = FindTemplateBounds(edges);
        var relativeEdgePoints = FindNonZeroPoints(edges)
            .Where(point => bounds.Contains(point))
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
            visibleMask: visibleMask,
            appearance: appearance);
    }
    internal static Mat CreateDistanceMap(
        MapStructureFeatures reference,
        double clip) =>
        reference.GetOrCreateClippedReferenceDistanceMap(clip);
    internal static Mat CreateDistanceMapFromEdges(
        Mat edges,
        double clip)
    {
        using var inverse = new Mat();
        Cv2.BitwiseNot(edges, inverse);
        var distance = new Mat();
        Cv2.DistanceTransform(
            inverse,
            distance,
            DistanceTypes.L2,
            DistanceTransformMasks.Precise);
        if (clip > 0d)
        {
            using var unclipped = distance;
            distance = unclipped.Clone();
            Cv2.Min(distance, clip, distance);
        }
        return distance;
    }
    internal static Point ExpectedReferenceLocation(
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
    internal static Rect CenteredSearchRect(
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
    internal static Point[] FindNonZeroPoints(Mat binary)
    {
        using var pointMatrix = new Mat();
        Cv2.FindNonZero(binary, pointMatrix);
        if (pointMatrix.Empty())
            return [];
        pointMatrix.GetArray(out Point[] points);
        return points;
    }

    internal sealed class ScaleSearchContext : IDisposable
    {
        public VisibleAwareCorrelationSession? VisibleAwareSession;
        public Mat? VisibleAwareReciprocalReference;
        public int VisibleAwareReciprocalFactor;
        public int VisibleAwareCompletedScales;
        public int VisibleAwareBudgetSkippedScales;
        public int VisibleAwareCoarsePeaks;
        public int VisibleAwareRefinedCandidates;
        public double VisibleAwareCoarseMs;
        public double VisibleAwareRefineMs;
        public readonly List<MapStructureCandidate> Candidates = new();
        public int ScalesEvaluated;
        public int SufficientlyStructuredHypotheses;
        public int OversizedHypotheses;
        public int FeatureMatchCount;
        public int FeatureInlierCount;
        public double QueryConstructionMs;
        public double HistoryCandidateMs;
        public double FeatureVotingMs;
        public double PyramidSearchMs;
        public double LocalTemplateSearchMs;
        public double GlobalTemplateSearchMs;
        public bool TimeBudgetExceeded;
        public bool WorkPreflightRejected;
        public int EstimatedRestrictedTemplateMilliseconds;
        public double VisibleAwareTotalMs;
        public int VisibleAwareCandidateCount;
        public double VisibleAwareBestCost = double.PositiveInfinity;
        public double VisibleAwareSecondCost = double.PositiveInfinity;
        public double VisibleAwareBestHypothesisScale;
        public double? VisibleAwareVisibleFraction;
        public int? VisibleAwareStructurePixels;
        public int? VisibleAwareEdgePixels;
        public bool VisibleAwareEarlyAccepted;
        public bool ScaleEarlyTerminated;
        public double ScaleEarlyTerminationConfidence;
        public string? VisibleAwareFallbackReason;
        public bool SkipLegacyCandidates;
        public double MarginNormalizationFloor = 0.01d;
        public void Dispose()
        {
            VisibleAwareSession?.Dispose();
            VisibleAwareReciprocalReference?.Dispose();
        }
        public double VisibleAwareTopMargin => double.IsPositiveInfinity(VisibleAwareSecondCost)
            ? 0d
            : Math.Clamp(
                (VisibleAwareSecondCost - VisibleAwareBestCost)
                / Math.Max(MarginNormalizationFloor, VisibleAwareSecondCost),
                0d, 1d);
    }

    internal const double ExtremelyHighConfidenceThreshold = 0.95d;

    internal static bool HasExtremelyHighConfidenceCandidate(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrationRequest request,
        out double confidence)
    {
        confidence = 0d;
        var ranked = MapStructureCandidateCollector.RankCandidatesByValidity(
            candidates,
            tuning,
            request.LockedTransform,
            request.RestrictSearchToLockedTransform,
            request).Valid;
        if (ranked.Length == 0)
            return false;

        var best = ranked[0];
        var secondScore = ranked.Length > 1
            ? ranked[1].CompositeCost
            : double.PositiveInfinity;
        if (!MapStructureValidator.MeetsEarlyTerminationCriteria(
                best,
                secondScore,
                tuning))
        {
            return false;
        }

        var margin = double.IsPositiveInfinity(secondScore)
            ? 1d
            : Math.Clamp(
                (secondScore - best.CompositeCost)
                    / Math.Max(
                        StructureRegistrationRules.MarginNormalizationFloor,
                        secondScore),
                0d,
                1d);
        var requiredMargin = tuning.MinimumCandidateMargin
            * (best.UsedGlobalSearch
                ? StructureRegistrationRules.GlobalSearchMarginMultiplier
                : 1d);
        if (MapStructureValidator.Validate(
                best,
                margin,
                requiredMargin,
                tuning,
                request.RestrictSearchToLockedTransform,
                request) != MapStructureRejectionReason.None)
        {
            return false;
        }

        confidence = MapStructureConfidenceCalculator.Calculate(
            best,
            margin,
            tuning,
            isTrackingMode: request.TrackingMode,
            sideEntrancePrior: request.SideEntrancePrior).FinalScore;
        return confidence >= ExtremelyHighConfidenceThreshold;
    }

    internal static void RequestEarlyTerminationForExtremelyHighConfidence(
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrationRequest request,
        ScaleSearchContext context)
    {
        if (tuning.DisableScaleEarlyTermination
            || !HasExtremelyHighConfidenceCandidate(
                context.Candidates,
                tuning,
                request,
                out var confidence))
        {
            return;
        }

        context.ScaleEarlyTerminated = true;
        context.ScaleEarlyTerminationConfidence = confidence;
        // RegisterLegacy checks this per-call value before starting the next
        // scale. The persisted tuning remains unchanged because it was cloned.
        tuning.EarlyTerminationScoreThreshold = double.PositiveInfinity;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构尺度搜索极高置信度早停 · {confidence:P0}");
    }

    internal static void SearchRestrictedBranch(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureFeatures live,
        MapStructureRegistrationRequest request,
        double scale,
        Point expected,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        ScaleSearchContext ctx,
        int remainingBudgetMilliseconds)
    {
        if (tuning.EnableFeatureVoting)
        {
            using var featureVoting = MapOperationTraceAmbient.StartChild(
                "feature_voting",
                MapOperationWaitKind.Compute);
            var featureTimer = Stopwatch.StartNew();
            MapStructureFeatureVoting.CollectFeatureCandidates(
                live, reference, query, request, scale, tuning,
                reciprocalScale, ctx.Candidates,
                out var matches, out var inliers);
            featureTimer.Stop();
            ctx.FeatureVotingMs += featureTimer.Elapsed.TotalMilliseconds;
            ctx.FeatureMatchCount = Math.Max(ctx.FeatureMatchCount, matches);
            ctx.FeatureInlierCount = Math.Max(ctx.FeatureInlierCount, inliers);
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
            scoreDomain, expected.X, expected.Y, radiusInReferencePixels);
        var estimatedTemplateMilliseconds = EstimateRestrictedTemplateMilliseconds(
            query,
            restrictedDomain);
        ctx.EstimatedRestrictedTemplateMilliseconds = Math.Max(
            ctx.EstimatedRestrictedTemplateMilliseconds,
            estimatedTemplateMilliseconds);
        var allowTemplateSearch = ShouldRunRestrictedTemplateSearch(
            request.Channel,
            estimatedTemplateMilliseconds,
            remainingBudgetMilliseconds,
            tuning.LowStructureWarmPathBudgetMilliseconds);
        if (!allowTemplateSearch)
        {
            ctx.TimeBudgetExceeded = true;
            ctx.WorkPreflightRejected = true;
        }
        using (var restrictedTemplate = MapOperationTraceAmbient.StartChild(
                   "restricted_template_search",
                   MapOperationWaitKind.Compute))
        {
            MapStructureCandidateCollector.SearchRestrictedCandidates(
                query, reference, referenceDistance, request, scale,
                expected, restrictedDomain, tuning,
                reciprocalScale, ctx.Candidates, allowTemplateSearch);
        }
        RequestEarlyTerminationForExtremelyHighConfidence(
            tuning,
            request,
            ctx);
    }
    internal static bool ShouldRunRestrictedTemplateSearch(
        MapAlignmentChannel channel,
        int estimatedMilliseconds,
        int remainingBudgetMilliseconds,
        int lowStructureWarmPathBudgetMilliseconds)
    {
        if (remainingBudgetMilliseconds < estimatedMilliseconds)
            return false;

        return channel != MapAlignmentChannel.LowStructure
            || estimatedMilliseconds <= lowStructureWarmPathBudgetMilliseconds / 3;
    }
    internal static int EstimateRestrictedTemplateMilliseconds(
        QueryGeometry query,
        Rect searchDomain)
    {
        const double baselineQueryPixels = 500_000d;
        const double baselineSearchPixels = 193d * 193d;
        const double baselineMilliseconds = 75d;
        var queryPixels = Math.Max(
            1d,
            query.Bounds.Width * (double)query.Bounds.Height);
        var searchPixels = Math.Max(
            1d,
            searchDomain.Width * (double)searchDomain.Height);
        var estimate = baselineMilliseconds
            * Math.Max(0.25d, queryPixels / baselineQueryPixels)
            * Math.Max(0.25d, searchPixels / baselineSearchPixels);
        return Math.Clamp((int)Math.Ceiling(estimate), 20, 400);
    }
}
/*
 * 文件职责：MapStructureScaleSearch。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
