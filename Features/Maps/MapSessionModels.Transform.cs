using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Maps full-reference pixels to physical screen pixels. Runtime alignment
/// always uses one uniform scale and one fixed rotation.
/// </summary>
public sealed class MapSimilarityTransform
{
    public double Scale { get; init; } = 1d;
    public double RotationDegrees { get; init; }
    public double TranslationX { get; init; }
    public double TranslationY { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Scale)
        && Scale > 0d
        && double.IsFinite(RotationDegrees)
        && double.IsFinite(TranslationX)
        && double.IsFinite(TranslationY);

    public MapScreenPoint ToScreen(MapReferencePoint point)
    {
        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new MapScreenPoint(
            ((point.X * cosine) - (point.Y * sine)) * Scale + TranslationX,
            ((point.X * sine) + (point.Y * cosine)) * Scale + TranslationY);
    }

    public MapReferencePoint ToReference(MapScreenPoint point)
    {
        if (!IsValid)
            return new MapReferencePoint(double.NaN, double.NaN);
        var scaledX = (point.X - TranslationX) / Scale;
        var scaledY = (point.Y - TranslationY) / Scale;
        var radians = -RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new MapReferencePoint(
            (scaledX * cosine) - (scaledY * sine),
            (scaledX * sine) + (scaledY * cosine));
    }

    public MapOverlayTransform ToOverlayTransform(
        int referenceWidth,
        int referenceHeight,
        double residualPixels = 0d) =>
        new()
        {
            ScaleX = Scale,
            ScaleY = Scale,
            OffsetX = TranslationX,
            OffsetY = TranslationY,
            ReferenceCenterX = referenceWidth / 2d,
            ReferenceCenterY = referenceHeight / 2d,
            ScreenCenterX = ToScreen(
                new MapReferencePoint(
                    referenceWidth / 2d,
                    referenceHeight / 2d)).X,
            ScreenCenterY = ToScreen(
                new MapReferencePoint(
                    referenceWidth / 2d,
                    referenceHeight / 2d)).Y,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            OrientationDegrees = NormalizeRotation(RotationDegrees),
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = Math.Max(0d, residualPixels)
        };

    public static MapSimilarityTransform FromOverlay(
        MapOverlayTransform transform) =>
        new()
        {
            Scale = (transform.ScaleX + transform.ScaleY) / 2d,
            RotationDegrees = transform.OrientationDegrees,
            TranslationX = transform.OffsetX,
            TranslationY = transform.OffsetY
        };

    private static int NormalizeRotation(double degrees)
    {
        var normalized = ((int)Math.Round(degrees) % 360 + 360) % 360;
        return normalized;
    }
}
/*
 * 文件职责：MapSessionModels.Transform。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
