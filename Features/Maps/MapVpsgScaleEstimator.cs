using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record MapVpsgScaleEstimate(
    double Scale,
    double OffsetX,
    double OffsetY,
    double Confidence,
    MapScaleEstimationEvidence Evidence);

/// <summary>
/// Viewport-Prior Scale Graph estimator. Descriptor matches identify graph
/// vertices; ratios between matched graph-edge lengths vote for scale without
/// requiring translation or trusting a previous floor's scale.
/// </summary>
public sealed class MapVpsgScaleEstimator
{
    public const double MinimumScale = 0.25d;
    public const double MaximumScale = 2.20d;
    public const int MinimumUniqueMatches = 12;
    public const int MinimumPairVotes = 24;
    public const double MaximumResidualPixels = 3d;
    public const double MaximumRotationDegrees = 2d;
    public const double MaximumRelativeMad = 0.015d;
    private const double RatioThreshold = 0.85d;
    private const double LogScaleBinRatio = 1.005d;
    private const double ClusterScaleTolerance = 0.015d;

    private sealed record PairVote(
        MapVpsgScaleGraphEdge Edge,
        DMatch First,
        DMatch Second,
        double Scale,
        double RotationDegrees,
        double Weight);

    private sealed record SimilarityFit(
        double Scale,
        double RotationDegrees,
        double OffsetX,
        double OffsetY,
        DMatch[] Inliers,
        double Residual);

    public bool TryEstimate(
        MapStructureFeatures reference,
        MapStructureFeatures live,
        MapVpsgScaleGraph graph,
        double priorScale,
        out MapVpsgScaleEstimate? estimate,
        out string rejectionReason)
    {
        estimate = null;
        rejectionReason = string.Empty;
        if (reference.Descriptors.Empty()
            || live.Descriptors.Empty()
            || reference.KeyPoints.Length != reference.Descriptors.Rows
            || live.KeyPoints.Length != live.Descriptors.Rows
            || reference.Descriptors.Type() != live.Descriptors.Type()
            || reference.Descriptors.Cols != live.Descriptors.Cols
            || !graph.IsCompatible(reference.Edges.Size(), reference.KeyPoints.Length))
        {
            rejectionReason = "incompatible AKAZE descriptors or scale graph";
            return false;
        }

        var matches = MatchReciprocal(reference.Descriptors, live.Descriptors);
        if (matches.Length < MinimumUniqueMatches)
        {
            rejectionReason = $"only {matches.Length} reciprocal AKAZE matches";
            return false;
        }

        var matchByReference = matches.ToDictionary(match => match.QueryIdx);
        var votes = BuildVotes(
            graph,
            matchByReference,
            reference.KeyPoints,
            live.KeyPoints);
        if (votes.Count < MinimumPairVotes)
        {
            rejectionReason = $"only {votes.Count} VPSG pair votes";
            return false;
        }

        var cluster = SelectScaleCluster(votes);
        var uniqueClusterMatches = cluster
            .SelectMany(vote => new[] { vote.First, vote.Second })
            .DistinctBy(match => (match.QueryIdx, match.TrainIdx))
            .ToArray();
        if (cluster.Count < MinimumPairVotes
            || uniqueClusterMatches.Length < MinimumUniqueMatches)
        {
            rejectionReason =
                $"unstable VPSG cluster: pairs={cluster.Count}, matches={uniqueClusterMatches.Length}";
            return false;
        }

        var fit = FindBestFit(
            cluster,
            matches,
            reference.KeyPoints,
            live.KeyPoints);
        if (fit is null)
        {
            rejectionReason = "no uniform VPSG similarity fit";
            return false;
        }

        var referenceSpan = PointSpan(fit.Inliers.Select(match =>
            reference.KeyPoints[match.QueryIdx].Pt));
        var liveSpan = PointSpan(fit.Inliers.Select(match =>
            live.KeyPoints[match.TrainIdx].Pt));
        var clusterMedian = Median(cluster.Select(vote => vote.Scale));
        var relativeMad = Median(cluster.Select(vote =>
            Math.Abs(vote.Scale - clusterMedian) / clusterMedian));
        if (fit.Inliers.Length < MinimumUniqueMatches
            || fit.Scale is < MinimumScale or > MaximumScale
            || Math.Abs(fit.RotationDegrees) > MaximumRotationDegrees
            || fit.Residual > MaximumResidualPixels
            || referenceSpan < 120d
            || liveSpan < 80d
            || relativeMad > MaximumRelativeMad)
        {
            rejectionReason =
                $"VPSG fit rejected: inliers={fit.Inliers.Length}, scale={fit.Scale:F4}, "
                + $"rotation={fit.RotationDegrees:F2}, residual={fit.Residual:F2}, "
                + $"span={referenceSpan:F0}/{liveSpan:F0}, mad={relativeMad:P2}";
            return false;
        }

        var priorAgreement = double.IsFinite(priorScale) && priorScale > 0d
            ? 1d - Math.Min(1d, Math.Abs(Math.Log(fit.Scale / priorScale)) / Math.Log(1.5d))
            : 0.5d;
        var confidence = Math.Clamp(
            0.62d
            + (Math.Min(1d, (fit.Inliers.Length - 8d) / 32d) * 0.16d)
            + (Math.Min(1d, referenceSpan / 400d) * 0.08d)
            + ((1d - Math.Min(1d, fit.Residual / MaximumResidualPixels)) * 0.08d)
            + ((1d - Math.Min(1d, relativeMad / MaximumRelativeMad)) * 0.04d)
            + (priorAgreement * 0.02d),
            0d,
            0.98d);
        var evidence = new MapScaleEstimationEvidence
        {
            UniqueMatches = fit.Inliers.Length,
            PairVotes = cluster.Count,
            ReferenceSpan = referenceSpan,
            LiveSpan = liveSpan,
            ResidualPixels = fit.Residual,
            RotationDegrees = fit.RotationDegrees,
            RelativeMedianAbsoluteDeviation = relativeMad
        };
        estimate = new MapVpsgScaleEstimate(
            fit.Scale,
            fit.OffsetX,
            fit.OffsetY,
            confidence,
            evidence);
        return true;
    }

    private static DMatch[] MatchReciprocal(Mat reference, Mat live)
    {
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var reverse = matcher.Match(live, reference)
            .GroupBy(match => match.QueryIdx)
            .ToDictionary(group => group.Key, group => group.MinBy(match => match.Distance)!);
        return matcher.KnnMatch(reference, live, 2)
            .Where(group =>
                group.Length >= 2
                && group[0].Distance < group[1].Distance * RatioThreshold)
            .Select(group => group[0])
            .Where(match =>
                reverse.TryGetValue(match.TrainIdx, out var reciprocal)
                && reciprocal.TrainIdx == match.QueryIdx)
            .OrderBy(match => match.Distance)
            .DistinctBy(match => match.TrainIdx)
            .Take(600)
            .ToArray();
    }

    // 用匹配点之间的动态最近邻边投票, 而不是预建图边。真实画面中匹配点往往
    // 聚集在参考图同一 4x4 网格 cell 内, 预建图"同 cell 不连边"规则会把聚集区
    // 内的边全部丢弃, 导致 votes 恒为 0(VPSG 退化为每次全尺度搜索)。动态边
    // 不受该限制: 在已匹配的参考点上按参考坐标取最近邻成边, 对局部聚集的匹配
    // 同样有效。graph 仅用于 TryEstimate 顶部的兼容性检查, 此处不再遍历。
    private static List<PairVote> BuildVotes(
        MapVpsgScaleGraph graph,
        IReadOnlyDictionary<int, DMatch> matches,
        IReadOnlyList<KeyPoint> referencePoints,
        IReadOnlyList<KeyPoint> livePoints)
    {
        const int nearestNeighborCount = 5;
        const double minimumReferenceDistance = 30d;

        var votes = new List<PairVote>();
        if (matches.Count < 4)
            return votes;

        var matched = matches.Values
            .OrderBy(match => match.QueryIdx)
            .ToArray();
        var referenceCoords = matched
            .Select(match => referencePoints[match.QueryIdx].Pt)
            .ToArray();

        for (var first = 0; first < matched.Length; first++)
        {
            var firstReference = referenceCoords[first];
            // 在参考距离 >= minimumReferenceDistance 的匹配点里取最近邻:
            // 密集分布下最近邻往往不足 30px(亚像素误差会被放大), 直接先滤掉。
            var nearest = Enumerable.Range(0, matched.Length)
                .Where(index => index != first)
                .Select(index => (Index: index, ReferenceDistance: SquaredDistance(
                    referenceCoords[index], firstReference)))
                .Where(candidate => candidate.ReferenceDistance
                    >= minimumReferenceDistance * minimumReferenceDistance)
                .OrderBy(candidate => candidate.ReferenceDistance)
                .Take(nearestNeighborCount)
                .Select(candidate => candidate.Index);
            foreach (var second in nearest)
            {
                var refDx = referenceCoords[second].X - firstReference.X;
                var refDy = referenceCoords[second].Y - firstReference.Y;
                var referenceDistance = Math.Sqrt((refDx * refDx) + (refDy * refDy));

                var firstLive = livePoints[matched[first].TrainIdx].Pt;
                var secondLive = livePoints[matched[second].TrainIdx].Pt;
                var dx = secondLive.X - firstLive.X;
                var dy = secondLive.Y - firstLive.Y;
                var liveDistance = Math.Sqrt((dx * dx) + (dy * dy));
                var scale = liveDistance / referenceDistance;
                if (scale is < MinimumScale or > MaximumScale)
                {
                    continue;
                }
                var liveAngle = Math.Atan2(dy, dx) * 180d / Math.PI;
                var referenceAngle = Math.Atan2(refDy, refDx) * 180d / Math.PI;
                var rotation = NormalizeAngle(liveAngle - referenceAngle);
                if (Math.Abs(rotation) > 5d)
                {
                    continue;
                }
                var weight = 1d / (1d + matched[first].Distance + matched[second].Distance);
                votes.Add(new PairVote(
                    new MapVpsgScaleGraphEdge(
                        matched[first].QueryIdx,
                        matched[second].QueryIdx,
                        referenceDistance,
                        referenceAngle),
                    matched[first],
                    matched[second],
                    scale,
                    rotation,
                    weight));
            }
        }
        return votes;
    }

    private static List<PairVote> SelectScaleCluster(IReadOnlyList<PairVote> votes)
    {
        var binSize = Math.Log(LogScaleBinRatio);
        var bins = votes.GroupBy(vote =>
                (int)Math.Round(Math.Log(vote.Scale) / binSize))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(vote => vote.Weight));
        var centerBin = bins.Keys
            .OrderByDescending(key => Enumerable.Range(key - 2, 5)
                .Sum(candidate => bins.GetValueOrDefault(candidate)))
            .First();
        var centerVotes = votes
            .Where(vote => Math.Abs(
                (int)Math.Round(Math.Log(vote.Scale) / binSize) - centerBin) <= 2)
            .ToArray();
        var centerScale = Median(centerVotes.Select(vote => vote.Scale));
        var centerRotation = Median(centerVotes.Select(vote => vote.RotationDegrees));
        return votes
            .Where(vote =>
                Math.Abs(vote.Scale - centerScale) / centerScale
                    <= ClusterScaleTolerance
                && Math.Abs(NormalizeAngle(vote.RotationDegrees - centerRotation))
                    <= MaximumRotationDegrees)
            .ToList();
    }

    private static SimilarityFit? FindBestFit(
        IReadOnlyList<PairVote> models,
        IReadOnlyList<DMatch> matches,
        IReadOnlyList<KeyPoint> referencePoints,
        IReadOnlyList<KeyPoint> livePoints)
    {
        SimilarityFit? best = null;
        foreach (var model in models
                     .OrderByDescending(candidate => candidate.Weight)
                     .Take(2000))
        {
            var radians = model.RotationDegrees * Math.PI / 180d;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            var reference = referencePoints[model.First.QueryIdx].Pt;
            var live = livePoints[model.First.TrainIdx].Pt;
            var offsetX = live.X - model.Scale *
                ((reference.X * cosine) - (reference.Y * sine));
            var offsetY = live.Y - model.Scale *
                ((reference.X * sine) + (reference.Y * cosine));
            var inliers = FindInliers(
                matches,
                referencePoints,
                livePoints,
                model.Scale,
                cosine,
                sine,
                offsetX,
                offsetY,
                MaximumResidualPixels);
            if (inliers.Length < MinimumUniqueMatches
                || (best is not null && inliers.Length < best.Inliers.Length))
            {
                continue;
            }
            var refined = FitSimilarity(inliers, referencePoints, livePoints);
            if (refined is null)
                continue;
            if (best is null
                || refined.Inliers.Length > best.Inliers.Length
                || (refined.Inliers.Length == best.Inliers.Length
                    && refined.Residual < best.Residual))
            {
                best = refined;
            }
        }
        return best;
    }

    private static SimilarityFit? FitSimilarity(
        DMatch[] seedInliers,
        IReadOnlyList<KeyPoint> referencePoints,
        IReadOnlyList<KeyPoint> livePoints)
    {
        var inliers = seedInliers;
        SimilarityFit? result = null;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var referenceMeanX = inliers.Average(match => referencePoints[match.QueryIdx].Pt.X);
            var referenceMeanY = inliers.Average(match => referencePoints[match.QueryIdx].Pt.Y);
            var liveMeanX = inliers.Average(match => livePoints[match.TrainIdx].Pt.X);
            var liveMeanY = inliers.Average(match => livePoints[match.TrainIdx].Pt.Y);
            var dot = 0d;
            var cross = 0d;
            var denominator = 0d;
            foreach (var match in inliers)
            {
                var reference = referencePoints[match.QueryIdx].Pt;
                var live = livePoints[match.TrainIdx].Pt;
                var rx = reference.X - referenceMeanX;
                var ry = reference.Y - referenceMeanY;
                var lx = live.X - liveMeanX;
                var ly = live.Y - liveMeanY;
                dot += (rx * lx) + (ry * ly);
                cross += (rx * ly) - (ry * lx);
                denominator += (rx * rx) + (ry * ry);
            }
            if (denominator <= 1d)
                return null;
            var a = dot / denominator;
            var b = cross / denominator;
            var scale = Math.Sqrt((a * a) + (b * b));
            var rotation = Math.Atan2(b, a) * 180d / Math.PI;
            var offsetX = liveMeanX - (a * referenceMeanX) + (b * referenceMeanY);
            var offsetY = liveMeanY - (b * referenceMeanX) - (a * referenceMeanY);
            var cosine = a / scale;
            var sine = b / scale;
            inliers = FindInliers(
                seedInliers,
                referencePoints,
                livePoints,
                scale,
                cosine,
                sine,
                offsetX,
                offsetY,
                MaximumResidualPixels);
            if (inliers.Length < MinimumUniqueMatches)
                return null;
            var residual = Math.Sqrt(inliers.Average(match =>
            {
                var error = Error(
                    match,
                    referencePoints,
                    livePoints,
                    scale,
                    cosine,
                    sine,
                    offsetX,
                    offsetY);
                return error * error;
            }));
            result = new SimilarityFit(
                scale,
                rotation,
                offsetX,
                offsetY,
                inliers,
                residual);
        }
        return result;
    }

    private static DMatch[] FindInliers(
        IEnumerable<DMatch> matches,
        IReadOnlyList<KeyPoint> referencePoints,
        IReadOnlyList<KeyPoint> livePoints,
        double scale,
        double cosine,
        double sine,
        double offsetX,
        double offsetY,
        double tolerance) =>
        matches
            .Where(match => Error(
                match,
                referencePoints,
                livePoints,
                scale,
                cosine,
                sine,
                offsetX,
                offsetY) <= tolerance)
            .ToArray();

    private static double Error(
        DMatch match,
        IReadOnlyList<KeyPoint> referencePoints,
        IReadOnlyList<KeyPoint> livePoints,
        double scale,
        double cosine,
        double sine,
        double offsetX,
        double offsetY)
    {
        var reference = referencePoints[match.QueryIdx].Pt;
        var live = livePoints[match.TrainIdx].Pt;
        var expectedX = offsetX + scale *
            ((reference.X * cosine) - (reference.Y * sine));
        var expectedY = offsetY + scale *
            ((reference.X * sine) + (reference.Y * cosine));
        return Math.Sqrt(
            Math.Pow(live.X - expectedX, 2d)
            + Math.Pow(live.Y - expectedY, 2d));
    }

    private static double PointSpan(IEnumerable<Point2f> points)
    {
        var array = points.ToArray();
        if (array.Length == 0)
            return 0d;
        var width = array.Max(point => point.X) - array.Min(point => point.X);
        var height = array.Max(point => point.Y) - array.Min(point => point.Y);
        return Math.Sqrt((width * width) + (height * height));
    }

    private static double SquaredDistance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180d)
            angle -= 360d;
        while (angle < -180d)
            angle += 360d;
        return angle;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return double.PositiveInfinity;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}
