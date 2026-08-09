using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record TranslationVote(
    double OffsetX,
    double OffsetY,
    double DescriptorDistance);

internal sealed record TranslationCluster(
    double OffsetX,
    double OffsetY,
    int InlierCount);

internal static class MapStructureFeatureVoting
{
    internal static void CollectFeatureCandidates(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        QueryGeometry query,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output,
        out int matchCount,
        out int maximumInliers)
    {
        matchCount = 0;
        maximumInliers = 0;
        if (live.Descriptors.Empty()
            || reference.Descriptors.Empty()
            || live.KeyPoints.Length == 0
            || reference.KeyPoints.Length == 0
            || live.Descriptors.Type() != reference.Descriptors.Type()
            || live.Descriptors.Cols != reference.Descriptors.Cols)
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
            if (votes.Count < StructureRegistrationRules.FeatureMinVotes)
                return;
            var tolerance = Math.Max(
                StructureRegistrationRules.FeatureMinInlierTolerance,
                tuning.FeatureInlierTolerancePixels);
            var clusters = votes
                .Select(seed =>
                {
                    var inliers = votes
                        .Where(vote => MapStructureEvaluator.Distance(
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
                            / Math.Max(StructureRegistrationRules.FeatureWeightEpsilon, weight),
                        inliers.Sum(vote =>
                                vote.OffsetY
                                / Math.Max(1d, vote.DescriptorDistance))
                            / Math.Max(StructureRegistrationRules.FeatureWeightEpsilon, weight),
                        inliers.Length);
                })
                .OrderByDescending(cluster => cluster.InlierCount)
                .ThenBy(cluster => MapStructureEvaluator.Distance(
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
                    && MapStructureEvaluator.Distance(
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
                var evaluated = MapStructureEvaluator.Evaluate(
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
                    tuning,
                    reciprocalScale);
                var consensus = Math.Clamp(
                    cluster.InlierCount / (double)Math.Max(StructureRegistrationRules.FeatureVoteCountDivisor, votes.Count),
                    0d,
                    1d);
                output.Add(evaluated with
                {
                    FeatureInlierCount = cluster.InlierCount,
                    FeatureConsensus = consensus,
                    CompositeCost = Math.Max(
                        0d,
                        evaluated.CompositeCost - (consensus * StructureRegistrationRules.FeatureConsensusCostReduction))
                });
            }
        }
        catch (OpenCVException)
        {
            matchCount = 0;
            maximumInliers = 0;
        }
    }
}
