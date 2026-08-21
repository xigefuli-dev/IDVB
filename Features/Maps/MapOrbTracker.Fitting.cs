using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapOrbTracker
{
    private static bool TryFitRotationFreeSimilarity(
        IReadOnlyList<Point2f> source,
        IReadOnlyList<Point2f> destination,
        IReadOnlyList<int> inliers,
        out double scale,
        out double translationX,
        out double translationY,
        out double medianError)
    {
        scale = translationX = translationY = medianError = double.NaN;
        if (inliers.Count < 3)
            return false;
        var sourceMeanX = inliers.Average(index => (double)source[index].X);
        var sourceMeanY = inliers.Average(index => (double)source[index].Y);
        var destinationMeanX = inliers.Average(index => (double)destination[index].X);
        var destinationMeanY = inliers.Average(index => (double)destination[index].Y);
        double numerator = 0;
        double denominator = 0;
        foreach (var index in inliers)
        {
            var sx = source[index].X - sourceMeanX;
            var sy = source[index].Y - sourceMeanY;
            numerator += (sx * (destination[index].X - destinationMeanX))
                + (sy * (destination[index].Y - destinationMeanY));
            denominator += (sx * sx) + (sy * sy);
        }
        if (denominator <= 1e-6)
            return false;
        scale = numerator / denominator;
        translationX = destinationMeanX - (scale * sourceMeanX);
        translationY = destinationMeanY - (scale * sourceMeanY);
        var fittedScale = scale;
        var fittedTranslationX = translationX;
        var fittedTranslationY = translationY;
        var errors = inliers
            .Select(index =>
            {
                var dx = destination[index].X
                    - ((fittedScale * source[index].X) + fittedTranslationX);
                var dy = destination[index].Y
                    - ((fittedScale * source[index].Y) + fittedTranslationY);
                return Math.Sqrt((dx * dx) + (dy * dy));
            })
            .OrderBy(value => value)
            .ToArray();
        medianError = errors.Length % 2 == 0
            ? (errors[(errors.Length / 2) - 1] + errors[errors.Length / 2]) / 2d
            : errors[errors.Length / 2];
        return double.IsFinite(scale)
            && double.IsFinite(translationX)
            && double.IsFinite(translationY)
            && double.IsFinite(medianError);
    }
}
/*
 * 文件职责：MapOrbTracker.Fitting。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
