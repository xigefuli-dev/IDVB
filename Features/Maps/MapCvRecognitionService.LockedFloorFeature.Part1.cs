using OpenCvSharp;
using OpenCvSharp.Features2D;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
public sealed partial class MapCvRecognitionService
{

    private static LockedFloorFeatureFit? TryFitLockedFloorFeatureCluster(
        IReadOnlyList<LockedFloorFeatureVote> cluster,
        IReadOnlyList<KeyPoint> referencePoints,
        IReadOnlyList<KeyPoint> livePoints,
        double priorScale)
    {
        var referenceMeanX = cluster.Average(vote =>
            referencePoints[vote.ReferenceIndex].Pt.X);
        var referenceMeanY = cluster.Average(vote =>
            referencePoints[vote.ReferenceIndex].Pt.Y);
        var liveMeanX = cluster.Average(vote => livePoints[vote.LiveIndex].Pt.X);
        var liveMeanY = cluster.Average(vote => livePoints[vote.LiveIndex].Pt.Y);
        var numerator = 0d;
        var denominator = 0d;
        foreach (var vote in cluster)
        {
            var referencePoint = referencePoints[vote.ReferenceIndex].Pt;
            var livePoint = livePoints[vote.LiveIndex].Pt;
            var referenceX = referencePoint.X - referenceMeanX;
            var referenceY = referencePoint.Y - referenceMeanY;
            numerator += (referenceX * (livePoint.X - liveMeanX))
                + (referenceY * (livePoint.Y - liveMeanY));
            denominator += (referenceX * referenceX)
                + (referenceY * referenceY);
        }
        if (denominator <= 1d)
            return null;
        var scaleFit = numerator / denominator;
        var offsetX = liveMeanX - (referenceMeanX * scaleFit);
        var offsetY = liveMeanY - (referenceMeanY * scaleFit);
        var inliers = cluster
            .Select(vote =>
            {
                var referencePoint = referencePoints[vote.ReferenceIndex].Pt;
                var livePoint = livePoints[vote.LiveIndex].Pt;
                var error = Math.Sqrt(
                    Math.Pow(
                        livePoint.X - (offsetX + (referencePoint.X * scaleFit)),
                        2d)
                    + Math.Pow(
                        livePoint.Y - (offsetY + (referencePoint.Y * scaleFit)),
                        2d));
                return (Vote: vote, Error: error);
            })
            .Where(item => item.Error <= LockedFloorFeatureMaximumResidual)
            .ToArray();
        if (inliers.Length < LockedFloorFeatureMinimumInliers)
            return null;

        var residual = Math.Sqrt(inliers.Average(item => item.Error * item.Error));
        var referenceSpan = PointSpan(
            inliers.Select(item =>
                referencePoints[item.Vote.ReferenceIndex].Pt));
        var liveSpan = PointSpan(
            inliers.Select(item => livePoints[item.Vote.LiveIndex].Pt));
        var scaleChange = double.IsFinite(priorScale) && priorScale > 0d
            ? Math.Abs((scaleFit / priorScale) - 1d)
            : 0d;
        if (scaleFit is < 0.25d or > 2d
            || scaleChange > LockedFloorFeatureMaximumScaleChange
            || residual > 2d
            || referenceSpan < 120d
            || liveSpan < 80d)
            return null;

        var descriptorDistance = inliers.Average(item =>
            item.Vote.DescriptorDistance);
        var confidence = Math.Clamp(
            0.80d
            + (Math.Min(1d, Math.Max(0d, (inliers.Length - 3d) / 9d)) * 0.04d)
            + (Math.Min(1d, referenceSpan / 180d) * 0.06d)
            + ((1d - Math.Min(1d, residual / 2d)) * 0.06d)
            + ((1d - Math.Min(1d, descriptorDistance / 300d)) * 0.04d),
            0d,
            0.98d);
        return new LockedFloorFeatureFit(
            scaleFit,
            offsetX,
            offsetY,
            inliers.Length,
            residual,
            referenceSpan,
            liveSpan,
            descriptorDistance,
            confidence);
    }

    private static LockedFloorFeatureFit? TryFitLockedFloorOrb(
        Mat reference,
        Mat live,
        double priorScale,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        using var liveGray = new Mat();
        switch (live.Channels())
        {
            case 1:
                live.CopyTo(liveGray);
                break;
            case 4:
                Cv2.CvtColor(live, liveGray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                Cv2.CvtColor(live, liveGray, ColorConversionCodes.BGR2GRAY);
                break;
        }
        using var orb = ORB.Create(
            nFeatures: 10000,
            scaleFactor: 1.1f,
            nLevels: 12,
            edgeThreshold: 31,
            firstLevel: 0,
            scoreType: ORBScoreType.Harris,
            patchSize: 31,
            fastThreshold: 5);
        using var referenceDescriptors = new Mat();
        using var liveDescriptors = new Mat();
        orb.DetectAndCompute(
            reference,
            null,
            out var referencePoints,
            referenceDescriptors);
        orb.DetectAndCompute(
            liveGray,
            null,
            out var livePoints,
            liveDescriptors);
        if (referenceDescriptors.Empty() || liveDescriptors.Empty())
        {
            rejectionReason = "no ORB descriptors";
            return null;
        }

        using var matcher = new BFMatcher(NormTypes.Hamming);
        var votes = matcher.KnnMatch(
                referenceDescriptors,
                liveDescriptors,
                2)
            .Where(group =>
                group.Length >= 2
                && group[0].Distance < group[1].Distance * 0.80d)
            .Select(group => group[0])
            .OrderBy(match => match.Distance)
            .Take(400)
            .Select(match => new LockedFloorFeatureVote(
                1d,
                0d,
                0d,
                match.QueryIdx,
                match.TrainIdx,
                match.Distance))
            .ToList();
        if (votes.Count < LockedFloorFeatureMinimumInliers)
        {
            rejectionReason = $"only {votes.Count} ORB ratio matches";
            return null;
        }

        var fits = new List<LockedFloorFeatureFit>();
        for (var firstIndex = 0; firstIndex < votes.Count - 1; firstIndex++)
        {
            var first = votes[firstIndex];
            var firstReference = referencePoints[first.ReferenceIndex].Pt;
            var firstLive = livePoints[first.LiveIndex].Pt;
            for (var secondIndex = firstIndex + 1;
                 secondIndex < votes.Count;
                 secondIndex++)
            {
                var second = votes[secondIndex];
                if (first.ReferenceIndex == second.ReferenceIndex
                    || first.LiveIndex == second.LiveIndex)
                {
                    continue;
                }
                var secondReference = referencePoints[second.ReferenceIndex].Pt;
                var secondLive = livePoints[second.LiveIndex].Pt;
                var referenceDeltaX = secondReference.X - firstReference.X;
                var referenceDeltaY = secondReference.Y - firstReference.Y;
                var liveDeltaX = secondLive.X - firstLive.X;
                var liveDeltaY = secondLive.Y - firstLive.Y;
                var referenceLengthSquared =
                    (referenceDeltaX * referenceDeltaX)
                    + (referenceDeltaY * referenceDeltaY);
                if (referenceLengthSquared < 6400d)
                    continue;
                var scale =
                    (double)((referenceDeltaX * liveDeltaX)
                    + (referenceDeltaY * liveDeltaY))
                    / referenceLengthSquared;
                if (scale is < 0.25d or > 2d
                    || (double.IsFinite(priorScale)
                        && priorScale > 0d
                        && Math.Abs((scale / priorScale) - 1d)
                            > LockedFloorFeatureMaximumScaleChange))
                {
                    continue;
                }
                var offsetX =
                    ((firstLive.X - (firstReference.X * scale))
                    + (secondLive.X - (secondReference.X * scale))) / 2d;
                var offsetY =
                    ((firstLive.Y - (firstReference.Y * scale))
                    + (secondLive.Y - (secondReference.Y * scale))) / 2d;
                var pairError = Math.Sqrt(
                    Math.Pow(firstLive.X - (offsetX + (firstReference.X * scale)), 2d)
                    + Math.Pow(firstLive.Y - (offsetY + (firstReference.Y * scale)), 2d)
                    + Math.Pow(secondLive.X - (offsetX + (secondReference.X * scale)), 2d)
                    + Math.Pow(secondLive.Y - (offsetY + (secondReference.Y * scale)), 2d));
                if (pairError > LockedFloorFeatureClusterTolerance)
                    continue;
                var cluster = votes
                    .Where(vote =>
                    {
                        var referencePoint = referencePoints[vote.ReferenceIndex].Pt;
                        var livePoint = livePoints[vote.LiveIndex].Pt;
                        return Math.Sqrt(
                            Math.Pow(livePoint.X - (offsetX + (referencePoint.X * scale)), 2d)
                            + Math.Pow(livePoint.Y - (offsetY + (referencePoint.Y * scale)), 2d))
                            <= LockedFloorFeatureClusterTolerance;
                    })
                    .DistinctBy(vote => vote.ReferenceIndex)
                    .DistinctBy(vote => vote.LiveIndex)
                    .ToArray();
                if (TryFitLockedFloorFeatureCluster(
                        cluster,
                        referencePoints,
                        livePoints,
                        priorScale) is { } fit)
                {
                    fits.Add(fit);
                }
            }
        }
        var best = fits
            .OrderByDescending(candidate => candidate.InlierCount)
            .ThenByDescending(candidate => candidate.ReferenceSpan)
            .ThenBy(candidate => candidate.Residual)
            .FirstOrDefault();
        if (best is null)
            rejectionReason = $"no reliable ORB fit from {votes.Count} matches";
        return best;
    }

    private static double PointSpan(IEnumerable<Point2f> points)
    {
        var values = points.ToArray();
        if (values.Length < 2)
            return 0d;
        var minX = values.Min(point => point.X);
        var maxX = values.Max(point => point.X);
        var minY = values.Min(point => point.Y);
        var maxY = values.Max(point => point.Y);
        return Math.Sqrt(
            Math.Pow(maxX - minX, 2d)
            + Math.Pow(maxY - minY, 2d));
    }
}
