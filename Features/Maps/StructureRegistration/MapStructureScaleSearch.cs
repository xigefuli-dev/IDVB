using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;
namespace IDVBuff.Features.Maps;
internal static class MapStructureScaleSearch
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
        var allowTemplateSearch = remainingBudgetMilliseconds
            >= estimatedTemplateMilliseconds;
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
        var visibleAwareSw = Stopwatch.StartNew();
        VisibleAwareSearchDiagnostics vaDiag;
        using (var visibleAware = MapOperationTraceAmbient.StartChild(
                   "visible_aware_search",
                   MapOperationWaitKind.Compute))
        {
            vaDiag = MapStructureVisibleAwareSearch.CollectVisibleAwareCandidates(
                query, reference, referenceDistance,
                request, scale, tuning,
                reciprocalScale, ctx, ctx.Candidates);
        }
        visibleAwareSw.Stop();
        if (vaDiag.Ran)
        {
            ctx.VisibleAwareCompletedScales++;
            ctx.VisibleAwareCoarsePeaks += vaDiag.CoarsePeakCount;
            ctx.VisibleAwareRefinedCandidates += vaDiag.RefinedCandidateCount;
            ctx.VisibleAwareCoarseMs += vaDiag.CoarseMilliseconds;
            ctx.VisibleAwareRefineMs += vaDiag.RefineMilliseconds;
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
        if (vaDiag.BudgetSkipped) ctx.VisibleAwareBudgetSkippedScales++;
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
            if (!isReciprocalScale)
            {
                using var pyramidSearch = MapOperationTraceAmbient.StartChild(
                    "pyramid_search",
                    MapOperationWaitKind.Compute);
                var pyramidTimer = Stopwatch.StartNew();
                MapStructurePyramidSearch.CollectPyramidCandidates(
                    query, reference, referenceDistance, request, scale, tuning,
                    reciprocalScale, ctx.Candidates);
                pyramidTimer.Stop();
                ctx.PyramidSearchMs += pyramidTimer.Elapsed.TotalMilliseconds;
            }
        }
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
            var localTemplate = MapOperationTraceAmbient.StartChild(
                "local_template_search",
                MapOperationWaitKind.Compute);
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
            localTemplate.Complete();
            using var globalTemplate = MapOperationTraceAmbient.StartChild(
                "global_template_search",
                MapOperationWaitKind.Compute);
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
                    var isLowStructure = request.Channel ==
                        MapAlignmentChannel.LowStructure;
                    var scaleDuplicateTolerance = isLowStructure
                        ? tuning.ScaleDuplicateTolerance
                        : StructureRegistrationRules.ScaleDuplicateTolerance;
                    var spatialDuplicateTolerance = isLowStructure
                        ? tuning.SpatialDuplicateTolerance
                        : StructureRegistrationRules.SpatialDuplicateTolerance;
                    if (!ctx.Candidates.Any(c =>
                            Math.Abs(c.Scale - scale)
                                < scaleDuplicateTolerance
                            && Math.Sqrt(
                                Math.Pow(c.ReferenceX - refX, 2)
                                + Math.Pow(c.ReferenceY - refY, 2))
                                < spatialDuplicateTolerance))
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
        using var fullTemplate = new Mat(query.Edges, query.Bounds);
        var isLowStructure =
            request.Channel == MapAlignmentChannel.LowStructure;
        using var paddedReferenceEdges = new Mat();
        using var paddedReferenceStructure = new Mat();
        using var paddedReferenceDistance = new Mat();
        var paddingX = 0;
        var paddingY = 0;
        Mat matchingReferenceEdges = reference.Edges;
        Mat matchingReferenceStructure = reference.StructureMask;
        Mat matchingReferenceDistance = referenceDistance;
        if (isLowStructure)
        {
            paddingX = query.Bounds.Width;
            paddingY = query.Bounds.Height;
            Cv2.CopyMakeBorder(
                reference.Edges,
                paddedReferenceEdges,
                paddingY,
                paddingY,
                paddingX,
                paddingX,
                BorderTypes.Constant,
                Scalar.Black);
            Cv2.CopyMakeBorder(
                reference.StructureMask,
                paddedReferenceStructure,
                paddingY,
                paddingY,
                paddingX,
                paddingX,
                BorderTypes.Constant,
                Scalar.Black);
            using var generatedDistance = CreateDistanceMapFromEdges(
                paddedReferenceEdges, tuning.DistanceClipPixels);
            generatedDistance.CopyTo(paddedReferenceDistance);
            matchingReferenceEdges = paddedReferenceEdges;
            matchingReferenceStructure = paddedReferenceStructure;
            matchingReferenceDistance = paddedReferenceDistance;
        }
        var minimumTemplateDimension = isLowStructure
            ? tuning.FastCoarseMinimumTemplateDimension
            : StructureRegistrationRules.CoarseFastCoarseMinTemplateDim;
        var D = tuning.FastCoarseDownsampleFactor;
        if (isLowStructure)
        {
            var maximumSafeDownsample = Math.Max(
                1,
                Math.Min(fullTemplate.Width, fullTemplate.Height)
                    / minimumTemplateDimension);
            D = Math.Clamp(D, 1, maximumSafeDownsample);
        }
        var targetWidth = Math.Max(1, fullTemplate.Width / D);
        var targetHeight = Math.Max(1, fullTemplate.Height / D);
        var refTargetWidth = Math.Max(1, matchingReferenceEdges.Width / D);
        var refTargetHeight = Math.Max(1, matchingReferenceEdges.Height / D);
        var maxDim = Math.Max(targetWidth, targetHeight);
        if (maxDim > tuning.FastCoarseMaxDimension)
        {
            var scaleForMaximum = (double)tuning.FastCoarseMaxDimension / maxDim;
            var extraScale = scaleForMaximum;
            if (isLowStructure)
            {
                var minDim = Math.Min(targetWidth, targetHeight);
                var scaleForMinimum = minDim > 0
                    ? (double)minimumTemplateDimension / minDim
                    : 1d;
                extraScale = Math.Max(scaleForMaximum, scaleForMinimum);
            }
            extraScale = Math.Min(1d, extraScale);
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * extraScale));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * extraScale));
            refTargetWidth = Math.Max(1, (int)Math.Round(refTargetWidth * extraScale));
            refTargetHeight = Math.Max(1, (int)Math.Round(refTargetHeight * extraScale));
        }
        if (targetWidth < minimumTemplateDimension
            || targetHeight < minimumTemplateDimension)
            return;
        using var template = new Mat();
        Cv2.Resize(fullTemplate, template,
            new Size(targetWidth, targetHeight),
            interpolation: InterpolationFlags.Area);
        using var refEdgesDown = new Mat();
        Cv2.Resize(matchingReferenceEdges, refEdgesDown,
            new Size(refTargetWidth, refTargetHeight),
            interpolation: InterpolationFlags.Area);
        if (isLowStructure)
        {
            Cv2.Threshold(
                refEdgesDown,
                refEdgesDown,
                0d,
                255d,
                ThresholdTypes.Binary);
        }
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
        var actualDownsampleX = (double)matchingReferenceEdges.Width / refTargetWidth;
        var actualDownsampleY = (double)matchingReferenceEdges.Height / refTargetHeight;
        var suppression = Math.Max(2, tuning.FastCoarseNmsRadius);
        for (var index = 0; index < tuning.FastCoarseTopK; index++)
        {
            Cv2.MinMaxLoc(scores, out var minimum, out _,
                out var location, out _);
            if (!double.IsFinite(minimum))
                break;
            var clampRefWidth = isLowStructure
                ? matchingReferenceEdges.Width
                : reciprocalScale.StructureMask?.Width ?? reference.Edges.Width;
            var clampRefHeight = isLowStructure
                ? matchingReferenceEdges.Height
                : reciprocalScale.StructureMask?.Height ?? reference.Edges.Height;
            var referenceX = Math.Clamp(
                (int)Math.Round(location.X * actualDownsampleX),
                0, clampRefWidth - query.Bounds.Width);
            var referenceY = Math.Clamp(
                (int)Math.Round(location.Y * actualDownsampleY),
                0, clampRefHeight - query.Bounds.Height);
            output.Add(MapStructureEvaluator.Evaluate(
                query, reference, referenceDistance,
                request, scale, referenceX, referenceY,
                usedGlobalSearch: !request.RestrictSearchToLockedTransform,
                tuning,
                reciprocalScale,
                matchingReferenceDistance,
                matchingReferenceStructure,
                paddingX,
                paddingY));
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
/*
 * 文件职责：MapStructureScaleSearch。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
