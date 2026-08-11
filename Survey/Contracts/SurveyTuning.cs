namespace IDVBuff.Survey.Contracts;

public sealed class SurveyCaptureTuning
{
    public int StableFrameDelayMilliseconds { get; set; } = 220;
    public int MaximumCaptureMilliseconds { get; set; } = 2500;
    public int QueueCapacity { get; set; } = 4;

    public void Validate()
    {
        if (StableFrameDelayMilliseconds is < 0 or > 5000)
            throw new InvalidDataException("survey.capture.stable_frame_delay_milliseconds 超出有效范围。");
        if (MaximumCaptureMilliseconds is < 250 or > 30000)
            throw new InvalidDataException("survey.capture.maximum_capture_milliseconds 超出有效范围。");
        if (QueueCapacity is < 1 or > 32)
            throw new InvalidDataException("survey.capture.queue_capacity 超出有效范围。");
    }
}

public sealed class SurveyPreprocessingTuning
{
    public int MaximumFeatureCount { get; set; } = 3000;
    public int EdgeLowThreshold { get; set; } = 50;
    public int EdgeHighThreshold { get; set; } = 150;
    public double MapCanvasLeft { get; set; } = 0.27d;
    public double MapCanvasTop { get; set; } = 0.24d;
    public double MapCanvasRight { get; set; } = 0.80d;
    public double MapCanvasBottom { get; set; } = 0.88d;
    public double ShapeOpeningDivisor { get; set; } = 420d;
    public double ShapeClosingDivisor { get; set; } = 520d;
    public double MinimumShapeComponentAreaRatio { get; set; } = 0.00012d;
    public double MinimumShapeThicknessFactor { get; set; } = 0.70d;
    public double MaximumShapeHoleAreaRatio { get; set; } = 0.00045d;

    public void Validate()
    {
        if (MaximumFeatureCount is < 250 or > 20000)
            throw new InvalidDataException("survey.preprocessing.maximum_feature_count 超出有效范围。");
        if (EdgeLowThreshold is < 0 or > 255
            || EdgeHighThreshold is < 1 or > 255
            || EdgeLowThreshold >= EdgeHighThreshold)
            throw new InvalidDataException("survey.preprocessing 的边缘阈值无效。");
        if (!double.IsFinite(MapCanvasLeft)
            || !double.IsFinite(MapCanvasTop)
            || !double.IsFinite(MapCanvasRight)
            || !double.IsFinite(MapCanvasBottom)
            || MapCanvasLeft < 0d
            || MapCanvasTop < 0d
            || MapCanvasRight > 1d
            || MapCanvasBottom > 1d
            || MapCanvasLeft >= MapCanvasRight
            || MapCanvasTop >= MapCanvasBottom)
            throw new InvalidDataException("survey.preprocessing 的可见区域颜色阈值无效。");
        if (!double.IsFinite(ShapeOpeningDivisor)
            || !double.IsFinite(ShapeClosingDivisor)
            || !double.IsFinite(MinimumShapeComponentAreaRatio)
            || !double.IsFinite(MinimumShapeThicknessFactor)
            || !double.IsFinite(MaximumShapeHoleAreaRatio)
            || ShapeOpeningDivisor is < 50d or > 4000d
            || ShapeClosingDivisor is < 50d or > 4000d
            || MinimumShapeComponentAreaRatio is <= 0d or > 0.05d
            || MinimumShapeThicknessFactor is < 0.1d or > 4d
            || MaximumShapeHoleAreaRatio is < 0d or > 0.05d)
            throw new InvalidDataException("survey.preprocessing 的可见区域掩码参数无效。");

    }
}

public sealed class SurveyRegistrationTuning
{
    public int CandidateCount { get; set; } = 4;
    public double RatioTest { get; set; } = 0.75d;
    public int MinimumMatches { get; set; } = 18;
    public int MinimumInliers { get; set; } = 12;
    public double MinimumInlierRatio { get; set; } = 0.45d;
    public double MaximumResidualPixels { get; set; } = 4.5d;
    public double MinimumScale { get; set; } = 0.75d;
    public double MaximumScale { get; set; } = 1.35d;

    public void Validate()
    {
        if (CandidateCount is < 1 or > 32)
            throw new InvalidDataException("survey.registration.candidate_count 超出有效范围。");
        if (RatioTest is <= 0.4d or >= 0.95d)
            throw new InvalidDataException("survey.registration.ratio_test 超出有效范围。");
        if (MinimumMatches is < 4 or > 1000 || MinimumInliers is < 3 || MinimumInliers > MinimumMatches)
            throw new InvalidDataException("survey.registration 的匹配数量门限无效。");
        if (MinimumInlierRatio is <= 0d or > 1d || MaximumResidualPixels is <= 0d)
            throw new InvalidDataException("survey.registration 的质量门限无效。");
        if (MinimumScale <= 0d || MaximumScale < MinimumScale)
            throw new InvalidDataException("survey.registration 的缩放范围无效。");
    }
}

public sealed class SurveyStorageTuning
{
    public int AssetRetentionDays { get; set; } = 30;
    public int MaximumProjectLayers { get; set; } = 2000;
    public int MinimumFreeSpaceMegabytes { get; set; } = 1024;

    public void Validate()
    {
        if (AssetRetentionDays is < 1 or > 3650
            || MaximumProjectLayers is < 1 or > 100000
            || MinimumFreeSpaceMegabytes is < 128)
            throw new InvalidDataException("survey.storage 配置无效。");
    }
}

public sealed class SurveyFusionTuning
{
    public int MaximumOutputPixels { get; set; } = 100_000_000;
    public double StructureBinaryThreshold { get; set; } = 0.5d;

    public void Validate()
    {
        if (MaximumOutputPixels is < 1_000_000 or > 500_000_000)
            throw new InvalidDataException("survey.fusion.visual.maximum_output_pixels 超出有效范围。");
        if (StructureBinaryThreshold is <= 0d or >= 1d)
            throw new InvalidDataException("survey.fusion.structure.binary_threshold 超出有效范围。");
    }
}
