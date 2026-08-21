namespace IDVBuff.Features.Maps;

/// <summary>Coordinate conversion used when the non-destructive recognition crop changes.</summary>
public static class MapRecognitionCoordinates
{
    public static void ApplyRecognitionRegion(
        FloorRecognitionProfile profile,
        NormalizedRectangle newRegion)
    {
        if (!newRegion.IsValid)
            throw new ArgumentException("Recognition region must be valid.", nameof(newRegion));

        var oldRegion = profile.GetEffectiveRecognitionRegion();
        foreach (var anchor in profile.Anchors.Where(anchor => anchor.Bounds?.IsValid is true))
        {
            var sourceBounds = ToSourceRectangle(anchor.Bounds!, oldRegion);
            anchor.Bounds = Contains(newRegion, sourceBounds)
                ? ToRegionRelativeRectangle(sourceBounds, newRegion)
                : null;
        }

        profile.RecognitionRegion = newRegion.Clone();
    }

    public static NormalizedRectangle ToSourceRectangle(
        NormalizedRectangle regionRelative,
        NormalizedRectangle region) => new()
    {
        X = region.X + (regionRelative.X * region.Width),
        Y = region.Y + (regionRelative.Y * region.Height),
        Width = regionRelative.Width * region.Width,
        Height = regionRelative.Height * region.Height
    };

    private static NormalizedRectangle ToRegionRelativeRectangle(
        NormalizedRectangle sourceRelative,
        NormalizedRectangle region) => new()
    {
        X = Math.Clamp((sourceRelative.X - region.X) / region.Width, 0d, 1d),
        Y = Math.Clamp((sourceRelative.Y - region.Y) / region.Height, 0d, 1d),
        Width = Math.Clamp(sourceRelative.Width / region.Width, 0d, 1d),
        Height = Math.Clamp(sourceRelative.Height / region.Height, 0d, 1d)
    };

    private static bool Contains(NormalizedRectangle outer, NormalizedRectangle inner)
    {
        const double epsilon = 0.000001d;
        return inner.X >= outer.X - epsilon
            && inner.Y >= outer.Y - epsilon
            && inner.X + inner.Width <= outer.X + outer.Width + epsilon
            && inner.Y + inner.Height <= outer.Y + outer.Height + epsilon;
    }
}
/*
 * 文件职责：MapRecognitionCoordinates。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
