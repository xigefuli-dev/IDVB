using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed class MapViewportStabilityTracker : IDisposable
{
    private Mat? _previous;
    private int _stableFrames;

    public int StableFrames => _stableFrames;
    public double LastDifference { get; private set; } = double.PositiveInfinity;

    public bool Observe(
        Mat viewport,
        double maximumDifference,
        int requiredFrames,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (viewport.Empty())
        {
            Reset();
            return false;
        }

        using var gray = new Mat();
        switch (viewport.Channels())
        {
            case 4:
                Cv2.CvtColor(
                    viewport,
                    gray,
                    ColorConversionCodes.BGRA2GRAY);
                break;
            case 3:
                Cv2.CvtColor(
                    viewport,
                    gray,
                    ColorConversionCodes.BGR2GRAY);
                break;
            default:
                viewport.CopyTo(gray);
                break;
        }
        using var normalized = new Mat();
        Cv2.Resize(
            gray,
            normalized,
            new Size(160, 100),
            interpolation: InterpolationFlags.Area);
        MaskSaturatedDynamicPixels(viewport, normalized);
        ApplyIgnoreRegions(normalized, ignoreRegions);
        var borderX = Math.Max(1, normalized.Width / 50);
        var borderY = Math.Max(1, normalized.Height / 50);
        using var content = new Mat(
            normalized,
            new Rect(
                borderX,
                borderY,
                normalized.Width - (borderX * 2),
                normalized.Height - (borderY * 2)));
        if (_previous is null || _previous.Size() != content.Size())
        {
            _previous?.Dispose();
            _previous = content.Clone();
            _stableFrames = 1;
            LastDifference = double.PositiveInfinity;
            return false;
        }

        using var difference = new Mat();
        Cv2.Absdiff(content, _previous, difference);
        LastDifference = Cv2.Mean(difference).Val0 / 255d;
        content.CopyTo(_previous);
        _stableFrames = LastDifference <= maximumDifference
            ? _stableFrames + 1
            : 1;
        return _stableFrames >= Math.Max(2, requiredFrames);
    }

    public void Reset()
    {
        _previous?.Dispose();
        _previous = null;
        _stableFrames = 0;
        LastDifference = double.PositiveInfinity;
    }

    public void Dispose() => Reset();

    private static void MaskSaturatedDynamicPixels(
        Mat source,
        Mat normalizedGray)
    {
        if (source.Channels() < 3)
            return;
        using var bgr = new Mat();
        if (source.Channels() == 4)
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        else
            source.CopyTo(bgr);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        try
        {
            using var saturated = new Mat();
            using var bright = new Mat();
            using var dynamicMask = new Mat();
            using var resizedMask = new Mat();
            Cv2.Threshold(
                channels[1],
                saturated,
                105d,
                255d,
                ThresholdTypes.Binary);
            Cv2.Threshold(
                channels[2],
                bright,
                70d,
                255d,
                ThresholdTypes.Binary);
            Cv2.BitwiseAnd(saturated, bright, dynamicMask);
            Cv2.Resize(
                dynamicMask,
                resizedMask,
                normalizedGray.Size(),
                interpolation: InterpolationFlags.Nearest);
            normalizedGray.SetTo(Scalar.Black, resizedMask);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private static void ApplyIgnoreRegions(
        Mat image,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions)
    {
        if (ignoreRegions is null)
            return;
        foreach (var region in ignoreRegions)
        {
            if (region?.IsValid is not true)
                continue;
            var left = Math.Clamp(
                (int)Math.Floor(region.X * image.Width),
                0,
                image.Width - 1);
            var top = Math.Clamp(
                (int)Math.Floor(region.Y * image.Height),
                0,
                image.Height - 1);
            var right = Math.Clamp(
                (int)Math.Ceiling(
                    (region.X + region.Width) * image.Width),
                left + 1,
                image.Width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling(
                    (region.Y + region.Height) * image.Height),
                top + 1,
                image.Height);
            Cv2.Rectangle(
                image,
                new Rect(left, top, right - left, bottom - top),
                Scalar.Black,
                -1);
        }
    }
}
/*
 * 文件职责：MapViewportStabilityTracker。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
