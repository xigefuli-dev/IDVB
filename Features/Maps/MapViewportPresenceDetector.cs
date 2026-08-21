using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record MapViewportColorSignature(
    IReadOnlyList<double> Histogram,
    double BlueGrayFraction,
    double MeanValue);

public sealed record MapViewportPresenceResult(
    bool IsPresent,
    string Mode,
    double Score,
    double BlueGrayFraction);

/// <summary>
/// Lightweight guard that distinguishes the native blue-gray map viewport
/// from a stable gameplay frame before the expensive alignment pipeline runs.
/// </summary>
public static class MapViewportPresenceDetector
{
    public const double MinimumReferenceSimilarity = 0.70d;
    public const double MinimumBlueGrayFraction = 0.60d;
    // 画面就绪（仅对齐放宽稳定等待）门槛：高于存在检测，用于挡开图动画帧。
    // 实测正常完整地图 reference 模式 min=0.982 / blue 模式 min=0.906，
    // 0.85 给足余量；动画早期帧更可能低于此被拒。
    public const double MinimumReadyReferenceSimilarity = 0.85d;
    public const double MinimumReadyBlueGrayFraction = 0.85d;
    // 就绪判定的明度一致性上限（归一化）：HSV 直方图不包含明度维度，淡入类
    // 动画早期帧颜色分布可与完整地图高度相似但整体偏暗。就绪判定要求候选与
    // 参考的 V 通道均值差 <= 该容差，否则按未就绪拒绝（实测调暗 0.4 差值 ~0.12）。
    public const double MaximumReadyBrightnessDelta = 0.10d;

    private const int HueBins = 18;
    private const int SaturationBins = 6;
    private const int SignatureWidth = 160;
    private const int SignatureHeight = 100;

    public static MapViewportColorSignature CreateSignature(Mat viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (viewport.Empty())
            return new MapViewportColorSignature([], 0d, 0d);

        using var bgr = new Mat();
        switch (viewport.Channels())
        {
            case 4:
                Cv2.CvtColor(viewport, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                viewport.CopyTo(bgr);
                break;
            default:
                Cv2.CvtColor(viewport, bgr, ColorConversionCodes.GRAY2BGR);
                break;
        }

        using var resized = new Mat();
        Cv2.Resize(
            bgr,
            resized,
            new Size(SignatureWidth, SignatureHeight),
            interpolation: InterpolationFlags.Area);
        using var hsv = new Mat();
        Cv2.CvtColor(resized, hsv, ColorConversionCodes.BGR2HSV);

        var histogram = new double[HueBins * SaturationBins];
        var blueGrayPixels = 0;
        var valueSum = 0d;
        var rows = hsv.Rows;
        var columns = hsv.Cols;
        var pixelCount = rows * columns;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var pixel = hsv.At<Vec3b>(y, x);
                var hueBin = Math.Min(HueBins - 1, pixel.Item0 * HueBins / 180);
                var saturationBin = Math.Min(
                    SaturationBins - 1,
                    pixel.Item1 * SaturationBins / 256);
                var index = (hueBin * SaturationBins) + saturationBin;
                histogram[index]++;

                if (pixel.Item0 is >= 90 and <= 130 && pixel.Item1 < 140)
                    blueGrayPixels++;
                valueSum += pixel.Item2;
            }
        }

        if (pixelCount > 0)
        {
            for (var i = 0; i < histogram.Length; i++)
                histogram[i] /= pixelCount;
        }

        return new MapViewportColorSignature(
            histogram,
            pixelCount > 0 ? blueGrayPixels / (double)pixelCount : 0d,
            pixelCount > 0 ? valueSum / pixelCount : 0d);
    }

    public static MapViewportPresenceResult Evaluate(
        Mat viewport,
        MapViewportColorSignature? reference = null) =>
        EvaluateCore(
            CreateSignature(viewport),
            reference,
            previousFrame: null,
            MinimumReferenceSimilarity,
            MinimumBlueGrayFraction,
            // 存在检测不校验明度（保持既有行为逐字节不变）。
            brightnessTolerance: double.MaxValue,
            requireBlueGrayBrightnessStability: false);

    /// <summary>
    /// 画面就绪判定（仅对齐放宽稳定等待专用）：与 Evaluate 同构，但阈值更高，
    /// 用于「已锁定地图重新开图」时逐帧判断画面是否已从动画过渡到完整地图。
    /// 参考签名优先（reference 非 null 时只走 reference-hsv，低分也不落 blue），
    /// 并要求候选与参考明度一致（挡淡入类动画帧）。reference 缺失时用更严的
    /// blue-gray 占比兜底，且因为 blue-gray 占比对等比例调暗天然不敏感（色相/
    /// 饱和度在均匀调暗下不变），额外要求候选帧与上一帧的明度也一致——没有
    /// previousFrame（循环第一帧）时按未就绪拒绝，强制至少两帧明度一致才放行。
    /// </summary>
    public static MapViewportPresenceResult EvaluateReady(
        Mat viewport,
        MapViewportColorSignature? reference = null,
        MapViewportColorSignature? previousFrame = null) =>
        EvaluateReady(
            CreateSignature(viewport),
            reference,
            previousFrame);

    /// <summary>
    /// 就绪判定的已算签名重载。就绪轮询每帧都要把本帧签名留作下一帧的明度
    /// 基线，用 Mat 重载会让同一帧的 HSV 直方图被计算两次。
    /// </summary>
    public static MapViewportPresenceResult EvaluateReady(
        MapViewportColorSignature candidate,
        MapViewportColorSignature? reference = null,
        MapViewportColorSignature? previousFrame = null) =>
        EvaluateCore(
            candidate,
            reference,
            previousFrame,
            MinimumReadyReferenceSimilarity,
            MinimumReadyBlueGrayFraction,
            brightnessTolerance: MaximumReadyBrightnessDelta,
            requireBlueGrayBrightnessStability: true);

    private static MapViewportPresenceResult EvaluateCore(
        MapViewportColorSignature candidate,
        MapViewportColorSignature? reference,
        MapViewportColorSignature? previousFrame,
        double referenceThreshold,
        double blueGrayThreshold,
        double brightnessTolerance,
        bool requireBlueGrayBrightnessStability)
    {
        if (reference is not null
            && reference.Histogram.Count == candidate.Histogram.Count
            && reference.Histogram.Count > 0)
        {
            var similarity = CosineSimilarity(
                reference.Histogram,
                candidate.Histogram);
            var brightnessDelta =
                Math.Abs(candidate.MeanValue - reference.MeanValue) / 255d;
            return new MapViewportPresenceResult(
                similarity >= referenceThreshold
                    && brightnessDelta <= brightnessTolerance,
                "reference-hsv",
                similarity,
                candidate.BlueGrayFraction);
        }

        var blueGrayReady = candidate.BlueGrayFraction >= blueGrayThreshold;
        if (requireBlueGrayBrightnessStability)
        {
            blueGrayReady = blueGrayReady
                && previousFrame is not null
                && Math.Abs(candidate.MeanValue - previousFrame.MeanValue) / 255d
                    <= brightnessTolerance;
        }

        return new MapViewportPresenceResult(
            blueGrayReady,
            "blue-gray-fallback",
            candidate.BlueGrayFraction,
            candidate.BlueGrayFraction);
    }

    private static double CosineSimilarity(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second)
    {
        var dot = 0d;
        var firstLength = 0d;
        var secondLength = 0d;
        for (var i = 0; i < first.Count; i++)
        {
            dot += first[i] * second[i];
            firstLength += first[i] * first[i];
            secondLength += second[i] * second[i];
        }

        var denominator = Math.Sqrt(firstLength * secondLength);
        return denominator > 1e-12 ? dot / denominator : 0d;
    }
}
/*
 * 文件职责：MapViewportPresenceDetector。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
