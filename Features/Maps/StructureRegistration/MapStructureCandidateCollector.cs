using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureCandidateCollector
{
    internal static void SearchRestrictedCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        Point expected,
        Rect searchDomain,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output,
        bool allowTemplateSearch = true)
    {
        var current = MapStructureEvaluator.Evaluate(
            query,
            reference,
            referenceDistance,
            request,
            scale,
            Math.Clamp(expected.X, searchDomain.X, searchDomain.Right - 1),
            Math.Clamp(expected.Y, searchDomain.Y, searchDomain.Bottom - 1),
            usedGlobalSearch: false,
            tuning,
            reciprocalScale);
        output.Add(current);
        if (IsStrongAbsoluteCandidate(current, tuning))
            return;
        if (!allowTemplateSearch)
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
            reciprocalScale,
            output,
            searchDomain.X,
            searchDomain.Y);
    }

    internal static void CollectHistoryCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        foreach (var transform in request.CandidateHistory
            .Where(candidate => candidate?.IsValid is true)
            .TakeLast(StructureRegistrationRules.MaxHistoryCandidates))
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
            // 互逆缩放：边界检查必须针对 referenceDistance 所在空间
            var histRefWidth = reciprocalScale.StructureMask?.Width
                ?? reference.Edges.Width;
            var histRefHeight = reciprocalScale.StructureMask?.Height
                ?? reference.Edges.Height;
            if (referenceX < 0
                || referenceY < 0
                || referenceX + query.Bounds.Width
                    >= histRefWidth
                || referenceY + query.Bounds.Height
                    >= histRefHeight)
            {
                continue;
            }
            output.Add(MapStructureEvaluator.Evaluate(
                query,
                reference,
                referenceDistance,
                request,
                scale,
                referenceX,
                referenceY,
                usedGlobalSearch: false,
                tuning,
                reciprocalScale));
        }
    }

    internal static bool IsStrongAbsoluteCandidate(
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
            StructureRegistrationRules.IsStrongCandidateMinPartitions,
            tuning.MinimumConsistentPartitions);

    internal static void CollectCandidates(
        Mat scores,
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        Rect searchRect,
        bool usedGlobalSearch,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output,
        int originX = 0,
        int originY = 0)
    {
        if (searchRect.Width <= 0 || searchRect.Height <= 0)
            return;
        using var search = new Mat(scores, searchRect).Clone();
        var suppressionRadius = Math.Max(
            tuning.MinimumSpanPixels,
            Math.Min(query.Bounds.Width, query.Bounds.Height) / StructureRegistrationRules.CollectCandidatesSuppressionDivisor);
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
            var isLowStructure =
                tuning.Channel == MapAlignmentChannel.LowStructure;
            var duplicateRadius = isLowStructure
                ? tuning.CandidateDuplicateRadius
                : StructureRegistrationRules.CandidateDuplicateRadius;
            if (!output.Any(candidate =>
                    Math.Abs(candidate.Scale - scale) <
                        (isLowStructure
                            ? tuning.ScaleDuplicateTolerance
                            : StructureRegistrationRules.ScaleDuplicateTolerance)
                    && Math.Sqrt(
                        Math.Pow(candidate.ReferenceX - referenceX, 2d)
                        + Math.Pow(candidate.ReferenceY - referenceY, 2d))
                        < duplicateRadius))
            {
                output.Add(MapStructureEvaluator.Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch,
                    tuning,
                    reciprocalScale));
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

    internal static IReadOnlyList<MapStructureCandidate> DistinctCandidates(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapOverlayTransform lockedTransform)
    {
        var distinct = new List<MapStructureCandidate>();
        foreach (var candidate in candidates
            .OrderBy(item => item.CompositeCost)
            .ThenBy(item => MapStructureEvaluator.Distance(
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

    internal static (
        MapStructureCandidate[] Ordered,
        MapStructureCandidate[] Diagnostic,
        MapStructureCandidate[] Valid) RankCandidatesByValidity(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapOverlayTransform lockedTransform,
        bool restrictedSearch)
    {
        var ordered = DistinctCandidates(candidates, tuning, lockedTransform)
            .OrderBy(candidate => candidate.CompositeCost)
            .ThenBy(candidate => MapStructureEvaluator.Distance(
                candidate.OffsetX,
                candidate.OffsetY,
                lockedTransform.OffsetX,
                lockedTransform.OffsetY))
            .ToArray();
        var diagnostic = ordered
            .Take(tuning.TopCandidateCount)
            .ToArray();
        var valid = ordered
            .Where(candidate => MapStructureValidator.ValidateAbsolute(
                candidate,
                tuning,
                restrictedSearch) == MapStructureRejectionReason.None)
            .Take(tuning.TopCandidateCount)
            .ToArray();
        return (ordered, diagnostic, valid);
    }
}
/*
 * 文件职责：MapStructureCandidateCollector。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
