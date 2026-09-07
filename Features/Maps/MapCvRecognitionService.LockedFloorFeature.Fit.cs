using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private static LockedFloorFeatureFit? TryFitLockedFloorFeature(
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
        using var sift = SIFT.Create(
            nFeatures: 5000,
            nOctaveLayers: 3,
            contrastThreshold: 0.01,
            edgeThreshold: 15d,
            sigma: 1.6d);
        using var referenceDescriptors = new Mat();
        using var liveDescriptors = new Mat();
        sift.DetectAndCompute(
            reference,
            null,
            out var referencePoints,
            referenceDescriptors);
        sift.DetectAndCompute(
            liveGray,
            null,
            out var livePoints,
            liveDescriptors);
        if (referenceDescriptors.Empty() || liveDescriptors.Empty())
        {
            rejectionReason = "no SIFT descriptors";
            return null;
        }

        using var matcher = new BFMatcher(NormTypes.L2);
        var groups = matcher.KnnMatch(
            referenceDescriptors,
            liveDescriptors,
            2);
        var votes = new List<LockedFloorFeatureVote>();
        foreach (var group in groups)
        {
            if (group.Length < 2 || group[0].Distance >= group[1].Distance * 0.82d)
                continue;
            var match = group[0];
            var referencePoint = referencePoints[match.QueryIdx];
            var livePoint = livePoints[match.TrainIdx];
            var scale = livePoint.Size / Math.Max(0.01d, referencePoint.Size);
            if (scale is < 0.25d or > 2d)
                continue;
            if (double.IsFinite(priorScale)
                && priorScale > 0d
                && Math.Abs((scale / priorScale) - 1d)
                    > LockedFloorFeatureMaximumScaleChange)
            {
                continue;
            }
            votes.Add(new LockedFloorFeatureVote(
                scale,
                livePoint.Pt.X - (referencePoint.Pt.X * scale),
                livePoint.Pt.Y - (referencePoint.Pt.Y * scale),
                match.QueryIdx,
                match.TrainIdx,
                match.Distance));
        }
        using var crossMatcher = new BFMatcher(NormTypes.L2, crossCheck: true);
        foreach (var match in crossMatcher.Match(
                     referenceDescriptors,
                     liveDescriptors))
        {
            if (match.Distance >= 250d)
                continue;
            var referencePoint = referencePoints[match.QueryIdx];
            var livePoint = livePoints[match.TrainIdx];
            var scale = livePoint.Size / Math.Max(0.01d, referencePoint.Size);
            if (scale is < 0.25d or > 2d
                || (double.IsFinite(priorScale)
                    && priorScale > 0d
                    && Math.Abs((scale / priorScale) - 1d)
                        > LockedFloorFeatureMaximumScaleChange))
            {
                continue;
            }
            votes.Add(new LockedFloorFeatureVote(
                scale,
                livePoint.Pt.X - (referencePoint.Pt.X * scale),
                livePoint.Pt.Y - (referencePoint.Pt.Y * scale),
                match.QueryIdx,
                match.TrainIdx,
                match.Distance));
        }
        votes = votes
            .GroupBy(vote => (vote.ReferenceIndex, vote.LiveIndex))
            .Select(group => group.MinBy(vote => vote.DescriptorDistance)!)
            .ToList();
        if (votes.Count < LockedFloorFeatureMinimumInliers)
        {
            rejectionReason = $"only {votes.Count} scale-consistent descriptor votes";
            return null;
        }

        var clusters = votes
            .Select(seed => votes
                .Where(vote =>
                    Math.Abs(Math.Log(vote.Scale / seed.Scale)) < 0.08d
                    && Math.Sqrt(
                        Math.Pow(vote.OffsetX - seed.OffsetX, 2d)
                        + Math.Pow(vote.OffsetY - seed.OffsetY, 2d)) < 35d)
                .OrderBy(vote => vote.DescriptorDistance)
                .DistinctBy(vote => vote.ReferenceIndex)
                .DistinctBy(vote => vote.LiveIndex)
                .ToArray())
            .ToList();
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
                var pairScale =
                    (double)((referenceDeltaX * liveDeltaX)
                    + (referenceDeltaY * liveDeltaY))
                    / referenceLengthSquared;
                if (pairScale is < 0.25d or > 2d
                    || (double.IsFinite(priorScale)
                        && priorScale > 0d
                        && Math.Abs((pairScale / priorScale) - 1d)
                            > LockedFloorFeatureMaximumScaleChange))
                {
                    continue;
                }
                var pairOffsetX =
                    ((firstLive.X - (firstReference.X * pairScale))
                    + (secondLive.X - (secondReference.X * pairScale))) / 2d;
                var pairOffsetY =
                    ((firstLive.Y - (firstReference.Y * pairScale))
                    + (secondLive.Y - (secondReference.Y * pairScale))) / 2d;
                var pairError = Math.Sqrt(
                    Math.Pow(firstLive.X - (pairOffsetX + (firstReference.X * pairScale)), 2d)
                    + Math.Pow(firstLive.Y - (pairOffsetY + (firstReference.Y * pairScale)), 2d)
                    + Math.Pow(firstLive.Y - (pairOffsetY + (firstReference.Y * pairScale)), 2d)
                    + Math.Pow(secondLive.X - (pairOffsetX + (secondReference.X * pairScale)), 2d)
                    + Math.Pow(secondLive.Y - (pairOffsetY + (secondReference.Y * pairScale)), 2d));
                if (pairError > LockedFloorFeatureClusterTolerance)
                    continue;
                clusters.Add(votes
                    .Where(vote =>
                    {
                        var referencePoint = referencePoints[vote.ReferenceIndex].Pt;
                        var livePoint = livePoints[vote.LiveIndex].Pt;
                        return Math.Sqrt(
                            Math.Pow(livePoint.X - (pairOffsetX + (referencePoint.X * pairScale)), 2d)
                            + Math.Pow(livePoint.Y - (pairOffsetY + (referencePoint.Y * pairScale)), 2d))
                            <= LockedFloorFeatureClusterTolerance;
                    })
                    .OrderBy(vote => vote.DescriptorDistance)
                    .DistinctBy(vote => vote.ReferenceIndex)
                    .DistinctBy(vote => vote.LiveIndex)
                    .ToArray());
            }
        }
        var fit = clusters
            .Where(cluster =>
                cluster.Length >= LockedFloorFeatureMinimumInliers)
            .Select(cluster => TryFitLockedFloorFeatureCluster(
                cluster,
                referencePoints,
                livePoints,
                priorScale))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.InlierCount)
            .ThenByDescending(candidate => candidate.ReferenceSpan)
            .ThenBy(candidate => candidate.Residual)
            .FirstOrDefault();
        if (fit is null)
        {
            var largestCluster = clusters.Max(cluster => cluster.Length);
            rejectionReason = $"no reliable uniform fit from {clusters.Count} clusters; largest={largestCluster}, votes={votes.Count}";
            return null;
        }
        return fit;
    }
}
