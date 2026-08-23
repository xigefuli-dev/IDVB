using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>侧门特征预处理结果，包含裁剪图和实际坐标信息。</summary>
public sealed class SideEntranceFeatureResult : IDisposable
{
    private bool _disposed;

    internal SideEntranceFeatureResult(
        Mat feature,
        double centerX,
        double centerY,
        int radius,
        int width,
        int height)
    {
        Feature = feature;
        CenterX = centerX;
        CenterY = centerY;
        Radius = radius;
        Width = width;
        Height = height;
    }

    /// <summary>预处理后的特征图（灰度）。</summary>
    public Mat Feature { get; }
    /// <summary>实际中心点 X（识别图像素坐标，边界挤压后）。</summary>
    public double CenterX { get; }
    /// <summary>实际中心点 Y（识别图像素坐标，边界挤压后）。</summary>
    public double CenterY { get; }
    /// <summary>实际裁剪半径（像素）。</summary>
    public int Radius { get; }
    /// <summary>特征图宽度（像素）。</summary>
    public int Width { get; }
    /// <summary>特征图高度（像素）。</summary>
    public int Height { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Feature.Dispose();
    }
}

/// <summary>
/// 从识别图裁剪以侧门为中心的特征图，支持边界挤压（中心偏移至裁剪框完全在图内）。
/// </summary>
public sealed class SideEntranceFeaturePreprocessor
{
    /// <summary>
    /// Increment when generated feature pixels or their matching semantics change.
    /// Persisted features from another version must be rebuilt before scanning.
    /// </summary>
    public const string AlgorithmVersion = "3-ratio-gate-masked";

    /// <summary>
    /// 处理侧门特征。
    /// </summary>
    /// <param name="recognitionImage">识别图（BGR 或灰度均可）。</param>
    /// <param name="anchorBounds">侧门锚点归一化坐标（相对识别图）。</param>
    /// <param name="featureRadius">目标特征半径（px）。</param>
    /// <returns>预处理结果（调用方负责 Dispose）。</returns>
    public SideEntranceFeatureResult Process(
        Mat recognitionImage,
        NormalizedRectangle anchorBounds,
        int featureRadius)
    {
        ArgumentNullException.ThrowIfNull(recognitionImage);
        if (featureRadius < 1)
            throw new ArgumentOutOfRangeException(nameof(featureRadius));

        return ProcessCore(
            recognitionImage,
            anchorBounds,
            featureWidth: checked(featureRadius * 2),
            featureHeight: checked(featureRadius * 2),
            clampToBounds: true,
            legacyRadius: featureRadius);
    }

    /// <summary>
    /// 按识别图宽高比例生成侧门特征。宽度和高度分别按比例计算，
    /// 因此不会把不同宽高比的识别图强行换算成固定像素或固定正方形。
    /// </summary>
    /// <param name="featureRegionRatio">相对识别图宽高的比例，例如 0.12。</param>
    /// <param name="clampToBounds">是否将中心向内挤压以保持裁剪框位于图像内。</param>
    public SideEntranceFeatureResult Process(
        Mat recognitionImage,
        NormalizedRectangle anchorBounds,
        double featureRegionRatio,
        bool clampToBounds)
    {
        ArgumentNullException.ThrowIfNull(recognitionImage);
        ArgumentNullException.ThrowIfNull(anchorBounds);
        if (!double.IsFinite(featureRegionRatio)
            || featureRegionRatio <= 0d
            || featureRegionRatio > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(featureRegionRatio));
        }

        var imageWidth  = recognitionImage.Width;
        var imageHeight = recognitionImage.Height;
        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentException("识别图尺寸无效。", nameof(recognitionImage));

        var featureWidth = Math.Max(1, (int)Math.Round(imageWidth * featureRegionRatio));
        var featureHeight = Math.Max(1, (int)Math.Round(imageHeight * featureRegionRatio));
        return ProcessCore(
            recognitionImage,
            anchorBounds,
            featureWidth,
            featureHeight,
            clampToBounds,
            legacyRadius: (int)Math.Ceiling(Math.Max(featureWidth, featureHeight) / 2d));
    }

    private static SideEntranceFeatureResult ProcessCore(
        Mat recognitionImage,
        NormalizedRectangle anchorBounds,
        int featureWidth,
        int featureHeight,
        bool clampToBounds,
        int legacyRadius)
    {
        ArgumentNullException.ThrowIfNull(recognitionImage);
        ArgumentNullException.ThrowIfNull(anchorBounds);

        var imageWidth  = recognitionImage.Width;
        var imageHeight = recognitionImage.Height;
        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentException("识别图尺寸无效。", nameof(recognitionImage));

        // 将归一化锚点中心换算为识别图像素坐标
        var cx = (anchorBounds.X + anchorBounds.Width  / 2d) * imageWidth;
        var cy = (anchorBounds.Y + anchorBounds.Height / 2d) * imageHeight;

        // 边界挤压是可选策略。关闭时保留锚点中心不变，超出图像的部分
        // 通过边缘裁剪并补齐到目标尺寸，避免既改变中心又导致 OpenCV ROI 越界。
        if (clampToBounds)
        {
            cx = ClampCenter(cx, featureWidth / 2d, imageWidth);
            cy = ClampCenter(cy, featureHeight / 2d, imageHeight);
        }

        var left = (int)Math.Round(cx - featureWidth / 2d);
        var top  = (int)Math.Round(cy - featureHeight / 2d);

        // 转灰度后裁剪
        using var gray = new Mat();
        if (recognitionImage.Channels() == 1)
            recognitionImage.CopyTo(gray);
        else
            Cv2.CvtColor(recognitionImage, gray, ColorConversionCodes.BGR2GRAY);

        var imageRect = new Rect(0, 0, imageWidth, imageHeight);
        var requestedRect = new Rect(left, top, featureWidth, featureHeight);
        var clippedRect = requestedRect.Intersect(imageRect);
        using var clipped = clippedRect.Width > 0 && clippedRect.Height > 0
            ? new Mat(gray, clippedRect).Clone()
            : new Mat();
        var fill = Cv2.Mean(gray).Val0;
        var feature = new Mat();
        Cv2.CopyMakeBorder(
            clipped,
            feature,
            Math.Max(0, -requestedRect.Y),
            Math.Max(0, requestedRect.Bottom - imageHeight),
            Math.Max(0, -requestedRect.X),
            Math.Max(0, requestedRect.Right - imageWidth),
            BorderTypes.Constant,
            new Scalar(fill));

        // Rounding at an image edge can leave one pixel missing from the pad.
        // Normalize the final dimensions without changing the stored center.
        if (feature.Width != featureWidth || feature.Height != featureHeight)
        {
            using var normalized = new Mat(feature, new Rect(
                Math.Max(0, (feature.Width - featureWidth) / 2),
                Math.Max(0, (feature.Height - featureHeight) / 2),
                Math.Min(featureWidth, feature.Width),
                Math.Min(featureHeight, feature.Height))).Clone();
            feature.Dispose();
            feature = new Mat();
            Cv2.CopyMakeBorder(
                normalized,
                feature,
                0,
                Math.Max(0, featureHeight - normalized.Height),
                0,
                Math.Max(0, featureWidth - normalized.Width),
                BorderTypes.Constant,
                new Scalar(fill));
        }

        // Every map uses the same side-gate glyph. Keeping that glyph in the
        // template makes it the strongest (and least discriminating) signal.
        // Replace only the annotated icon rectangle with the surrounding mean;
        // the live scan applies the same operation to the detected gate.
        var anchorLeft = (int)Math.Floor(anchorBounds.X * imageWidth) - left;
        var anchorTop = (int)Math.Floor(anchorBounds.Y * imageHeight) - top;
        var anchorWidth = (int)Math.Ceiling(anchorBounds.Width * imageWidth);
        var anchorHeight = (int)Math.Ceiling(anchorBounds.Height * imageHeight);
        var iconRect = new Rect(
            Math.Clamp(anchorLeft, 0, Math.Max(0, feature.Width - 1)),
            Math.Clamp(anchorTop, 0, Math.Max(0, feature.Height - 1)),
            Math.Clamp(anchorWidth, 1, Math.Max(1, feature.Width - Math.Clamp(anchorLeft, 0, Math.Max(0, feature.Width - 1)))),
            Math.Clamp(anchorHeight, 1, Math.Max(1, feature.Height - Math.Clamp(anchorTop, 0, Math.Max(0, feature.Height - 1)))));
        if (iconRect.Width > 0 && iconRect.Height > 0)
        {
            var mean = Cv2.Mean(feature);
            Cv2.Rectangle(feature, iconRect, new Scalar(mean.Val0), -1);
        }
        return new SideEntranceFeatureResult(
            feature,
            cx,
            cy,
            legacyRadius,
            featureWidth,
            featureHeight);
    }

    /// <summary>
    /// 将中心坐标平移，使指定半范围尽量处于 [0, dimension) 内。
    /// </summary>
    private static double ClampCenter(double center, double halfExtent, int dimension)
    {
        if (halfExtent * 2d >= dimension)
            return dimension / 2d;
        return Math.Clamp(center, halfExtent, dimension - halfExtent);
    }
}
/*
 * 文件职责：SideEntranceFeaturePreprocessor。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
