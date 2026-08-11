using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static class MapStructureScaleSearch
{
    internal static IReadOnlyList<double> BuildScaleHypotheses(
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

    // ═══════════════════════════════════════════════════════════════
    // 缩放搜索上下文：在 RegisterLegacy 迭代中携带跨假设的状态
    // ═══════════════════════════════════════════════════════════════

    internal sealed class ScaleSearchContext
    {
        public readonly List<MapStructureCandidate> Candidates = new();
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

        // Visible-aware 诊断累加器
        public double VisibleAwareTotalMs;
        public int VisibleAwareCandidateCount;
        public double VisibleAwareBestCost = double.PositiveInfinity;
        public double VisibleAwareSecondCost = double.PositiveInfinity;
        public double VisibleAwareBestHypothesisScale;
        public double? VisibleAwareVisibleFraction;
        public int? VisibleAwareStructurePixels;
        public int? VisibleAwareEdgePixels;
        public bool VisibleAwareEarlyAccepted;
        public string? VisibleAwareFallbackReason;
        public bool SkipLegacyCandidates;

        public double VisibleAwareTopMargin => double.IsPositiveInfinity(VisibleAwareSecondCost)
            ? 0d
            : Math.Clamp(
                (VisibleAwareSecondCost - VisibleAwareBestCost)
                / Math.Max(StructureRegistrationRules.MarginNormalizationFloor, VisibleAwareSecondCost),
                0d, 1d);
    }

    // ═══════════════════════════════════════════════════════════════
    // 受限搜索分支：ORB 特征投票 + 局部模板匹配
    // ═══════════════════════════════════════════════════════════════

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
        ScaleSearchContext ctx)
    {
        if (tuning.EnableFeatureVoting)
        {
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
        MapStructureCandidateCollector.SearchRestrictedCandidates(
            query, reference, referenceDistance, request, scale,
            expected, restrictedDomain, tuning,
            reciprocalScale, ctx.Candidates);
    }

    // ═══════════════════════════════════════════════════════════════
    // 全局搜索分支：Visible-aware → ORB → 金字塔 → 局部模板 → 全局降采样
    // ═══════════════════════════════════════════════════════════════

    internal static void SearchGlobalBranch(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureFeatures live,
        MapStructureRegistrationRequest request,
        double scale,
        Point expected,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        bool isReciprocalScale,
        ScaleSearchContext ctx)
    {
        // Step 1: Visible-aware 快速候选
        var visibleAwareSw = Stopwatch.StartNew();
        var vaDiag = MapStructureVisibleAwareSearch.CollectVisibleAwareCandidates(
            query, reference, referenceDistance,
            request, scale, tuning,
            reciprocalScale, ctx.Candidates);
        visibleAwareSw.Stop();
        if (vaDiag.Ran)
        {
            ctx.VisibleAwareTotalMs += visibleAwareSw.Elapsed.TotalMilliseconds;
            ctx.VisibleAwareCandidateCount += vaDiag.CandidateCount;
            ctx.VisibleAwareVisibleFraction ??= vaDiag.VisibleFraction;
            ctx.VisibleAwareStructurePixels ??= vaDiag.VisibleStructurePixels;
            ctx.VisibleAwareEdgePixels ??= vaDiag.VisibleEdgePixels;
            if (vaDiag.BestCost < ctx.VisibleAwareBestCost)
            {
                ctx.VisibleAwareBestCost = vaDiag.BestCost;
                ctx.VisibleAwareBestHypothesisScale = scale;
                ctx.VisibleAwareSecondCost = vaDiag.SecondCost;
            }
        }

        // Step 2: 判断是否提前终止
        if (tuning.EnableVisibleAwareEarlyExit
            && tuning.VisibleAwareEarlyTerminationMaxCompositeCost > 0d)
        {
            var visibleAwareCandidates = ctx.Candidates
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
                if (MapStructureValidator.MeetsEarlyTerminationCriteria(
                        visibleAwareBest, secondBestCost, tuning))
                {
                    ctx.SkipLegacyCandidates = true;
                    ctx.VisibleAwareEarlyAccepted = true;
                }
                else
                {
                    ctx.VisibleAwareFallbackReason =
                        "Visible-aware best below cost threshold but fails " +
                        "individual validation criteria";
                }
            }
            else if (visibleAwareBest is not null)
            {
                ctx.VisibleAwareFallbackReason =
                    $"Visible-aware best composite cost " +
                    $"{visibleAwareBest.CompositeCost:F3} exceeds threshold " +
                    $"{tuning.VisibleAwareEarlyTerminationMaxCompositeCost:F3}";
            }
            else
            {
                ctx.VisibleAwareFallbackReason =
                    "No visible-aware candidates found for early termination";
            }
        }

        // Step 3: Legacy 候选生成（由 skipLegacyCandidates 门控）
        if (!ctx.SkipLegacyCandidates)
        {
            var bestFastCost = ctx.Candidates.Count == 0
                ? double.PositiveInfinity
                : ctx.Candidates.Min(candidate => candidate.CompositeCost);
            var shouldRunFeatureVoting = tuning.EnableFeatureVoting
                && !isReciprocalScale
                && (ctx.Candidates.Count == 0
                    || bestFastCost > tuning.EarlyTerminationScoreThreshold);
            if (shouldRunFeatureVoting)
            {
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
            if (!isReciprocalScale)
            {
                var pyramidTimer = Stopwatch.StartNew();
                MapStructurePyramidSearch.CollectPyramidCandidates(
                    query, reference, referenceDistance, request, scale, tuning,
                    reciprocalScale, ctx.Candidates);
                pyramidTimer.Stop();
                ctx.PyramidSearchMs += pyramidTimer.Elapsed.TotalMilliseconds;
            }
        }

        // Step 4: 模板匹配（由 skipLegacyCandidates 门控）
        if (!ctx.SkipLegacyCandidates)
        {
            using var template = new Mat(query.Edges, query.Bounds);
            using var templateFloat = new Mat();
            template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
            var localRadius = Math.Max(
                tuning.MinimumSpanPixels,
                (int)Math.Round(
                    Math.Max(reference.Edges.Width, reference.Edges.Height)
                    * tuning.LocalSearchRadiusRatio));

            // 局部搜索
            var localTimer = Stopwatch.StartNew();
            var localRefX = Math.Max(0, expected.X - localRadius);
            var localRefY = Math.Max(0, expected.Y - localRadius);
            var localRefW = Math.Min(
                referenceDistance.Width - localRefX,
                Math.Max(1, expected.X + localRadius + template.Width - localRefX));
            var localRefH = Math.Min(
                referenceDistance.Height - localRefY,
                Math.Max(1, expected.Y + localRadius + template.Height - localRefY));

            if (localRefW > template.Width && localRefH > template.Height)
            {
                using var localRefPatch = new Mat(
                    referenceDistance,
                    new Rect(localRefX, localRefY, localRefW, localRefH));
                using var localScores = new Mat();
                Cv2.MatchTemplate(
                    localRefPatch, templateFloat, localScores,
                    TemplateMatchModes.CCorr);
                Cv2.Multiply(
                    localScores,
                    1d / Math.Max(1, query.EdgeCount),
                    localScores);

                MapStructureCandidateCollector.CollectCandidates(
                    localScores, query, reference, referenceDistance,
                    request, scale,
                    new Rect(0, 0, localScores.Width, localScores.Height),
                    usedGlobalSearch: false, tuning, reciprocalScale,
                    ctx.Candidates,
                    originX: localRefX, originY: localRefY);
            }
            localTimer.Stop();
            ctx.LocalTemplateSearchMs += localTimer.Elapsed.TotalMilliseconds;

            // 2x 降采样全局搜索
            var globalDsTimer = Stopwatch.StartNew();
            var dsFactor = StructureRegistrationRules.CoarseDownsampleFactor;
            var dsRefW = Math.Max(1, referenceDistance.Width / dsFactor);
            var dsRefH = Math.Max(1, referenceDistance.Height / dsFactor);
            var dsTplW = Math.Max(1, template.Width / dsFactor);
            var dsTplH = Math.Max(1, template.Height / dsFactor);
            var useDownsampled = dsRefW >= StructureRegistrationRules.CoarseMinRefDimension
                && dsRefH >= StructureRegistrationRules.CoarseMinRefDimension
                && dsTplW >= StructureRegistrationRules.CoarseMinTplDimension
                && dsTplH >= StructureRegistrationRules.CoarseMinTplDimension;

            if (useDownsampled)
            {
                using var dsRefDist = new Mat();
                Cv2.Resize(referenceDistance, dsRefDist,
                    new Size(dsRefW, dsRefH),
                    interpolation: InterpolationFlags.Area);

                using var dsTemplate = new Mat();
                Cv2.Resize(template, dsTemplate,
                    new Size(dsTplW, dsTplH),
                    interpolation: InterpolationFlags.Area);
                using var dsTplFloat = new Mat();
                dsTemplate.ConvertTo(dsTplFloat, MatType.CV_32FC1, 1d / 255d);

                using var dsScores = new Mat();
                Cv2.MatchTemplate(
                    dsRefDist, dsTplFloat, dsScores,
                    TemplateMatchModes.CCorr);
                Cv2.Multiply(
                    dsScores,
                    1d / Math.Max(1, query.EdgeCount),
                    dsScores);

                var coordScaleX = (double)referenceDistance.Width / dsRefW;
                var coordScaleY = (double)referenceDistance.Height / dsRefH;
                var dsSuppression = Math.Max(
                    1,
                    Math.Max(tuning.MinimumSpanPixels,
                        Math.Min(query.Bounds.Width, query.Bounds.Height)
                        / StructureRegistrationRules.CoarseSuppressionDivisor)
                    / dsFactor);

                for (var gi = 0; gi < tuning.TopCandidateCount; gi++)
                {
                    Cv2.MinMaxLoc(dsScores,
                        out var minVal, out _,
                        out var minLoc, out _);
                    if (!double.IsFinite(minVal))
                        break;
                    var refX = Math.Clamp(
                        (int)Math.Round(minLoc.X * coordScaleX),
                        0,
                        referenceDistance.Width - query.Bounds.Width);
                    var refY = Math.Clamp(
                        (int)Math.Round(minLoc.Y * coordScaleY),
                        0,
                        referenceDistance.Height - query.Bounds.Height);
                    if (!ctx.Candidates.Any(c =>
                            Math.Abs(c.Scale - scale)
                                < StructureRegistrationRules.ScaleDuplicateTolerance
                            && Math.Sqrt(
                                Math.Pow(c.ReferenceX - refX, 2)
                                + Math.Pow(c.ReferenceY - refY, 2))
                                < StructureRegistrationRules.SpatialDuplicateTolerance))
                    {
                        ctx.Candidates.Add(MapStructureEvaluator.Evaluate(
                            query, reference, referenceDistance,
                            request, scale, refX, refY,
                            usedGlobalSearch: true, tuning,
                            reciprocalScale));
                    }
                    var left = Math.Max(0, minLoc.X - dsSuppression);
                    var top = Math.Max(0, minLoc.Y - dsSuppression);
                    Cv2.Rectangle(
                        dsScores,
                        new Rect(
                            left, top,
                            Math.Min(dsScores.Width - left,
                                dsSuppression * 2 + 1),
                            Math.Min(dsScores.Height - top,
                                dsSuppression * 2 + 1)),
                        Scalar.All(double.PositiveInfinity),
                        -1);
                }
            }
            else
            {
                // 小图回退：全分辨率全局搜索
                using var fullScores = new Mat();
                Cv2.MatchTemplate(
                    referenceDistance, templateFloat, fullScores,
                    TemplateMatchModes.CCorr);
                Cv2.Multiply(
                    fullScores,
                    1d / Math.Max(1, query.EdgeCount),
                    fullScores);
                MapStructureCandidateCollector.CollectCandidates(
                    fullScores, query, reference, referenceDistance,
                    request, scale,
                    new Rect(0, 0, fullScores.Width, fullScores.Height),
                    usedGlobalSearch: true, tuning, reciprocalScale,
                    ctx.Candidates);
            }
            globalDsTimer.Stop();
            ctx.GlobalTemplateSearchMs += globalDsTimer.Elapsed.TotalMilliseconds;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 快速粗搜索候选项收集（提取自 MapStructureRegistrar）
    // ═══════════════════════════════════════════════════════════════

    internal static void CollectFastCoarseCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        var D = tuning.FastCoarseDownsampleFactor;
        using var fullTemplate = new Mat(query.Edges, query.Bounds);

        var targetWidth = Math.Max(1, fullTemplate.Width / D);
        var targetHeight = Math.Max(1, fullTemplate.Height / D);
        var refTargetWidth = Math.Max(1, reference.Edges.Width / D);
        var refTargetHeight = Math.Max(1, reference.Edges.Height / D);

        var maxDim = Math.Max(targetWidth, targetHeight);
        if (maxDim > tuning.FastCoarseMaxDimension)
        {
            // Capping only the long side can collapse a long, thin viewport
            // below the minimum template dimension.  That used to make the
            // fast path return NoCandidate without running a search at all.
            // Preserve enough pixels on the short side even when that means
            // allowing the long side to exceed the preferred performance cap.
            var minDim = Math.Min(targetWidth, targetHeight);
            var scaleForMaximum = (double)tuning.FastCoarseMaxDimension / maxDim;
            var scaleForMinimum = minDim > 0
                ? (double)StructureRegistrationRules.CoarseFastCoarseMinTemplateDim / minDim
                : 1d;
            var extraScale = Math.Min(1d, Math.Max(scaleForMaximum, scaleForMinimum));
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * extraScale));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * extraScale));
            refTargetWidth = Math.Max(1, (int)Math.Round(refTargetWidth * extraScale));
            refTargetHeight = Math.Max(1, (int)Math.Round(refTargetHeight * extraScale));
        }
        if (targetWidth < StructureRegistrationRules.CoarseFastCoarseMinTemplateDim
            || targetHeight < StructureRegistrationRules.CoarseFastCoarseMinTemplateDim)
            return;

        using var template = new Mat();
        Cv2.Resize(fullTemplate, template,
            new Size(targetWidth, targetHeight),
            interpolation: InterpolationFlags.Area);

        using var refEdgesDown = new Mat();
        Cv2.Resize(reference.Edges, refEdgesDown,
            new Size(refTargetWidth, refTargetHeight),
            interpolation: InterpolationFlags.Area);
        using var inverse = new Mat();
        Cv2.BitwiseNot(refEdgesDown, inverse);
        using var coarseDistMap = new Mat();
        Cv2.DistanceTransform(inverse, coarseDistMap,
            DistanceTypes.L2, DistanceTransformMasks.Mask3);

        using var templateFloat = new Mat();
        template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
        using var scores = new Mat();
        Cv2.MatchTemplate(coarseDistMap, templateFloat, scores,
            TemplateMatchModes.CCorr);
        var edgePixelCount = Math.Max(1, Cv2.CountNonZero(template));
        Cv2.Multiply(scores, 1d / edgePixelCount, scores);

        var actualDownsampleX = (double)reference.Edges.Width / refTargetWidth;
        var actualDownsampleY = (double)reference.Edges.Height / refTargetHeight;

        var suppression = Math.Max(2, tuning.FastCoarseNmsRadius);
        for (var index = 0; index < tuning.FastCoarseTopK; index++)
        {
            Cv2.MinMaxLoc(scores, out var minimum, out _,
                out var location, out _);
            if (!double.IsFinite(minimum))
                break;

            var clampRefWidth = reciprocalScale.StructureMask?.Width
                ?? reference.Edges.Width;
            var clampRefHeight = reciprocalScale.StructureMask?.Height
                ?? reference.Edges.Height;
            var referenceX = Math.Clamp(
                (int)Math.Round(location.X * actualDownsampleX),
                0, clampRefWidth - query.Bounds.Width);
            var referenceY = Math.Clamp(
                (int)Math.Round(location.Y * actualDownsampleY),
                0, clampRefHeight - query.Bounds.Height);

            output.Add(MapStructureEvaluator.Evaluate(
                query, reference, referenceDistance,
                request, scale, referenceX, referenceY,
                usedGlobalSearch: true, tuning, reciprocalScale));

            var left = Math.Max(0, location.X - suppression);
            var top = Math.Max(0, location.Y - suppression);
            var right = Math.Min(scores.Width, location.X + suppression + 1);
            var bottom = Math.Min(scores.Height, location.Y + suppression + 1);
            Cv2.Rectangle(scores,
                new Rect(left, top, right - left, bottom - top),
                Scalar.All(double.PositiveInfinity), -1);
        }
    }
}
