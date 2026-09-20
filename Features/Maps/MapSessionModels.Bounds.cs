using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class MapReferenceBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1d;
    public double Height { get; set; } = 1d;

    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0d
        && Height > 0d;

    public MapReferenceBounds Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height
    };

    public static MapReferenceBounds FullImage(int width, int height) => new()
    {
        Width = Math.Max(1, width),
        Height = Math.Max(1, height)
    };

    public bool Contains(MapReferencePoint point, double tolerance = 0d) =>
        point.IsFinite
        && point.X >= X - tolerance
        && point.Y >= Y - tolerance
        && point.X <= Right + tolerance
        && point.Y <= Bottom + tolerance;

    public MapReferencePoint Clamp(MapReferencePoint point) => new(
        Math.Clamp(point.X, X, Right),
        Math.Clamp(point.Y, Y, Bottom));

    public MapViewportOrigin ClampViewportOrigin(
        MapViewportOrigin origin,
        double viewportWidth,
        double viewportHeight)
    {
        if (!IsValid
            || !origin.IsFinite
            || !double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0d
            || viewportHeight <= 0d)
        {
            return new MapViewportOrigin(X, Y);
        }

        // A native map canvas can be larger than the projected reference map.
        // In that case the valid origin interval is reversed: the reference
        // may sit anywhere between the canvas's left/top and right/bottom
        // edges while remaining fully visible.
        var minimumX = Math.Min(X, Right - viewportWidth);
        var maximumX = Math.Max(X, Right - viewportWidth);
        var minimumY = Math.Min(Y, Bottom - viewportHeight);
        var maximumY = Math.Max(Y, Bottom - viewportHeight);
        return new MapViewportOrigin(
            Math.Clamp(origin.X, minimumX, maximumX),
            Math.Clamp(origin.Y, minimumY, maximumY));
    }
}
/*
 * 文件职责：MapSessionModels.Bounds。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
