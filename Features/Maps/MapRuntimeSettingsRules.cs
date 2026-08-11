namespace IDVBuff.Features.Maps;

/// <summary>User-adjustable recognition thresholds persisted with runtime settings.</summary>
public sealed class MapRecognitionTuning
{
    public const double DefaultGateTemplateThreshold = 0.72d;
    public const double DefaultMinimumConfidence = 0.50d;
    public const double DefaultVectorErrorTolerance = 0.15d;
    public const double DefaultAmbiguityMargin = 0.015d;
    public const double DefaultConfirmationAdvantage = 0.08d;
    public const int DefaultSideEntranceFeatureRadius = 80;

    public double GateTemplateThreshold { get; set; } = DefaultGateTemplateThreshold;
    public double MinimumConfidence { get; set; } = DefaultMinimumConfidence;
    public double VectorErrorTolerance { get; set; } = DefaultVectorErrorTolerance;
    public double AmbiguityMargin { get; set; } = DefaultAmbiguityMargin;
    public double ConfirmationAdvantage { get; set; } = DefaultConfirmationAdvantage;
    public bool ForceBestRecognitionResult { get; set; } = false;
    public bool ForceCandidateSelection { get; set; } = true;
    /// <summary>
    /// 开启后每次扫描成功都会弹出变换窗口，由玩家决定叠加地图的缩放与位置，
    /// 确认结果直接渲染并以最高信任来源写入缩放缓存。
    /// </summary>
    public bool PlayerDecidesScale { get; set; } = false;

    public int WarmGateSearchBudgetMs { get; set; } = 120;
    public int ConfirmationGateSearchBudgetMs { get; set; }

    public double ConfirmationRoiTemplatePaddingFactor { get; set; } = 1.0d;
    public int ConfirmationRoiMinimumPaddingPixels { get; set; } = 24;
    /// <summary>Maximum map drag speed in pixels/second, used to size confirmation ROI.</summary>
    public double ConfirmationMaximumMapDragPixelsPerSecond { get; set; } = 600d;
    /// <summary>Scheduling slack for confirmation ROI (frame interval + capture delay).</summary>
    public int ConfirmationSchedulingSlackMilliseconds { get; set; } = 100;
    /// <summary>侧门特征图半径（识别图像素）。修改后需重新预处理所有地图的侧门特征。</summary>
    public int SideEntranceFeatureRadius { get; set; } = DefaultSideEntranceFeatureRadius;

    [System.Text.Json.Serialization.JsonIgnore]
    public int ConfirmationMaximumMotionPixels =>
        (int)Math.Round(
            ConfirmationMaximumMapDragPixelsPerSecond
            * ConfirmationSchedulingSlackMilliseconds
            / 1000d);

    public MapRecognitionTuning Clone() => new()
    {
        GateTemplateThreshold = GateTemplateThreshold,
        MinimumConfidence = MinimumConfidence,
        VectorErrorTolerance = VectorErrorTolerance,
        AmbiguityMargin = AmbiguityMargin,
        ConfirmationAdvantage = ConfirmationAdvantage,
        ForceBestRecognitionResult = ForceBestRecognitionResult,
        ForceCandidateSelection = ForceCandidateSelection,
        PlayerDecidesScale = PlayerDecidesScale,
        WarmGateSearchBudgetMs = WarmGateSearchBudgetMs,
        ConfirmationGateSearchBudgetMs = ConfirmationGateSearchBudgetMs,
        ConfirmationRoiTemplatePaddingFactor = ConfirmationRoiTemplatePaddingFactor,
        ConfirmationRoiMinimumPaddingPixels = ConfirmationRoiMinimumPaddingPixels,
        ConfirmationMaximumMapDragPixelsPerSecond =
            ConfirmationMaximumMapDragPixelsPerSecond,
        ConfirmationSchedulingSlackMilliseconds =
            ConfirmationSchedulingSlackMilliseconds,
        SideEntranceFeatureRadius = SideEntranceFeatureRadius
    };

    public void Normalize()
    {
        GateTemplateThreshold = NormalizeFinite(
            GateTemplateThreshold,
            DefaultGateTemplateThreshold,
            0.50d,
            0.95d);
        MinimumConfidence = NormalizeFinite(
            MinimumConfidence,
            DefaultMinimumConfidence,
            0.20d,
            0.95d);
        VectorErrorTolerance = NormalizeFinite(
            VectorErrorTolerance,
            DefaultVectorErrorTolerance,
            0.01d,
            2.0d);
        AmbiguityMargin = NormalizeFinite(
            AmbiguityMargin,
            DefaultAmbiguityMargin,
            0.001d,
            0.05d);
        ConfirmationAdvantage = NormalizeFinite(
            ConfirmationAdvantage,
            DefaultConfirmationAdvantage,
            0.01d,
            0.25d);
        WarmGateSearchBudgetMs = Math.Clamp(WarmGateSearchBudgetMs, 0, 1000);
        ConfirmationGateSearchBudgetMs = Math.Clamp(
            ConfirmationGateSearchBudgetMs,
            0,
            500);
        ConfirmationRoiTemplatePaddingFactor = NormalizeFinite(
            ConfirmationRoiTemplatePaddingFactor,
            1.0d,
            0.5d,
            3.0d);
        ConfirmationRoiMinimumPaddingPixels = Math.Clamp(
            ConfirmationRoiMinimumPaddingPixels,
            8,
            100);
        ConfirmationMaximumMapDragPixelsPerSecond = NormalizeFinite(
            ConfirmationMaximumMapDragPixelsPerSecond,
            600d,
            100d,
            3000d);
        ConfirmationSchedulingSlackMilliseconds = Math.Clamp(
            ConfirmationSchedulingSlackMilliseconds,
            30,
            500);
        SideEntranceFeatureRadius = Math.Clamp(
            SideEntranceFeatureRadius,
            20,
            500);
    }

    private static double NormalizeFinite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed class MapFloorRecognitionTuning
{
    public double MinimumConfidence { get; set; } =
        FloorRecognitionRules.DefaultMinimumConfidence;
    public double MinimumLocalizationConfidence { get; set; } =
        FloorRecognitionRules.DefaultMinimumLocalizationConfidence;
    public int MaximumRecognitionWindowMilliseconds { get; set; } =
        FloorRecognitionRules.DefaultRecognitionWindowMilliseconds;
    public int FirstFloorConfirmationFrames { get; set; } =
        FloorRecognitionRules.DefaultFirstFloorConfirmationFrames;
    public int SecondFloorConfirmationFrames { get; set; } =
        FloorRecognitionRules.DefaultSecondFloorConfirmationFrames;

    public MapFloorRecognitionTuning Clone() => new()
    {
        MinimumConfidence = MinimumConfidence,
        MinimumLocalizationConfidence = MinimumLocalizationConfidence,
        MaximumRecognitionWindowMilliseconds = MaximumRecognitionWindowMilliseconds,
        FirstFloorConfirmationFrames = FirstFloorConfirmationFrames,
        SecondFloorConfirmationFrames = SecondFloorConfirmationFrames
    };

    public void Normalize()
    {
        MinimumConfidence = NormalizeFinite(
            MinimumConfidence,
            FloorRecognitionRules.DefaultMinimumConfidence,
            0.30d,
            0.99d);
        MinimumLocalizationConfidence = NormalizeFinite(
            MinimumLocalizationConfidence,
            FloorRecognitionRules.DefaultMinimumLocalizationConfidence,
            0.30d,
            0.99d);
        MaximumRecognitionWindowMilliseconds = Math.Clamp(
            MaximumRecognitionWindowMilliseconds,
            500,
            10000);
        FirstFloorConfirmationFrames = Math.Clamp(
            FirstFloorConfirmationFrames,
            1,
            8);
        SecondFloorConfirmationFrames = Math.Clamp(
            SecondFloorConfirmationFrames,
            1,
            8);
    }

    private static double NormalizeFinite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed class MapPlayerTrackingTuning
{
    public double MinimumConfidence { get; set; } =
        PlayerTrackingRules.DefaultMinimumConfidence;
    public int LocalSearchFailureLimit { get; set; } =
        PlayerTrackingRules.DefaultLocalSearchFailureLimit;
    public int StaleHideMilliseconds { get; set; } =
        PlayerTrackingRules.DefaultStaleHideMilliseconds;

    public MapPlayerTrackingTuning Clone() => new()
    {
        MinimumConfidence = MinimumConfidence,
        LocalSearchFailureLimit = LocalSearchFailureLimit,
        StaleHideMilliseconds = StaleHideMilliseconds
    };

    public void Normalize()
    {
        MinimumConfidence = NormalizeFinite(
            MinimumConfidence,
            PlayerTrackingRules.DefaultMinimumConfidence,
            0.30d,
            0.99d);
        LocalSearchFailureLimit = Math.Clamp(LocalSearchFailureLimit, 1, 20);
        StaleHideMilliseconds = Math.Clamp(StaleHideMilliseconds, 100, 5000);
    }

    private static double NormalizeFinite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

/// <summary>Platform-neutral normalization rules for persisted map runtime settings.</summary>
public static class MapRuntimeSettingsRules
{
    public const int CurrentCalibrationVersion = 2;

    public static MapOverlayAlignmentMode NormalizeAlignmentMode(MapOverlayAlignmentMode mode) =>
        Enum.IsDefined(mode) ? mode : MapOverlayAlignmentMode.IndependentAxes;

    public static bool IsCalibrationCurrent(
        bool regionIsValid,
        int clientWidth,
        int clientHeight,
        int calibrationVersion) =>
        regionIsValid
        && clientWidth > 0
        && clientHeight > 0
        && calibrationVersion >= CurrentCalibrationVersion;
}
