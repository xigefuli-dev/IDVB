using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructurePyramidSearch
{
    internal static void CollectPyramidCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        if (reference.EdgePyramid.Count < StructureRegistrationRules.PyramidMinLevels)
            return;
        using var fullTemplate = new Mat(query.Edges, query.Bounds);
        var targetWidth = Math.Max(1, fullTemplate.Width / StructureRegistrationRules.PyramidDownsampleFactor);
        var targetHeight = Math.Max(1, fullTemplate.Height / StructureRegistrationRules.PyramidDownsampleFactor);
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
            var referenceX = location.X * StructureRegistrationRules.PyramidDownsampleFactor;
            var referenceY = location.Y * StructureRegistrationRules.PyramidDownsampleFactor;
            if (referenceX + query.Bounds.Width < reference.Edges.Width
                && referenceY + query.Bounds.Height < reference.Edges.Height)
            {
                output.Add(MapStructureEvaluator.Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch: true,
                    tuning,
                    reciprocalScale));
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
}
/*
 * 文件职责：MapStructurePyramidSearch。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
