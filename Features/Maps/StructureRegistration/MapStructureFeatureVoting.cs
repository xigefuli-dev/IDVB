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
/*
 * 文件职责：MapStructureFeatureVoting。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
