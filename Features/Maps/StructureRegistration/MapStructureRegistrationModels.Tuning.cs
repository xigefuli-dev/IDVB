using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public enum MapAuxiliaryAnchorRecognitionMode
{
    Off = 0,
    AmbiguityOnly = 1,
    Always = 2
}

public sealed class MapStructureRegistrationTuning
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; set; }
    public MapAuxiliaryAnchorRecognitionMode AuxiliaryAnchorMode { get; set; } =
        MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly;

    /// <summary>
    /// Source-compatible view of the former Boolean setting. New persisted
    /// settings use <see cref="AuxiliaryAnchorMode"/> so anchors can be
    /// reserved for ambiguous alignments instead of running on every frame.
    /// </summary>
    [JsonIgnore]
    public bool UseAuxiliaryAnchorRecognition
    {
        get => AuxiliaryAnchorMode != MapAuxiliaryAnchorRecognitionMode.Off;
        set => AuxiliaryAnchorMode = value
            ? MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly
            : MapAuxiliaryAnchorRecognitionMode.Off;
    }
    public bool ReusePreviousAlignmentResult { get; set; } = true;
    public int MaximumAuxiliaryTemplates { get; set; } = 4;
    public double AuxiliaryDirectLockConfidence { get; set; } = 0.82d;
    public int StructureFallbackBudgetMilliseconds { get; set; } = 1500;
    public int PreviousAlignmentSearchRadiusPixels { get; set; } = 96;
    public int TrackingSearchRadiusPixels { get; set; } = 48;
    public double TrackingScaleSearchRadius { get; set; } = 0.005d;
    public double EarlyTerminationScoreThreshold { get; set; } = 0.55d;
    public double SkipEccScoreThreshold { get; set; } = 8d;
    public int MinimumEdgePixels { get; set; } = 90;
    public int MinimumSpanPixels { get; set; } = 28;
    public int MinimumConsistentPartitions { get; set; } = 2;
    public int TopCandidateCount { get; set; } = 6;
    public double MaximumChamferPixels { get; set; } = 3.2d;
    /// <summary>
    /// 受限搜索(RestrictSearchToLockedTransform=true)专用的独立 chamfer 上限。
    /// 不受分辨率档 TOML 的 MaximumChamferPixels 放宽影响——受限窗口很小,
    /// 真位置在窗口内时 chamfer 必然低,假候选(部分重叠)chamfer 显著偏高。
    /// </summary>
    public double RestrictedSearchMaximumChamferPixels { get; set; } = 3.0d;
    public double MinimumEdgeCoverage { get; set; } = 0.40d;
    public double MinimumOccupancyCoverage { get; set; } = 0.42d;
    public double MinimumCandidateMargin { get; set; } = 0.04d;
    public double LocalSearchRadiusRatio { get; set; } = 0.20d;
    public double ScaleSearchRadius { get; set; } = 0.02d;
    public double ScaleSearchStep { get; set; } = 0.01d;
    public double EdgeDistanceTolerancePixels { get; set; } = 2.25d;
    public double DistanceClipPixels { get; set; } = 12d;
    public bool EnableDebugOutput { get; set; }
    public bool EnableEccRefinement { get; set; }
    public bool EnableFeatureVoting { get; set; } = true;
    public int MaximumTranslationCandidates { get; set; } = 5;
    public double FeatureRatioThreshold { get; set; } = 0.64d;
    public double FeatureInlierTolerancePixels { get; set; } = 6d;
    public double MaximumPlayerPriorDistanceRatio { get; set; } = 0.45d;
    public double MapViewportEdgeMargin { get; set; } = 0.20d;

    // ═══════════════════════════════════════════════════════════════
    // Visible-aware 实验开关（生产默认全部关闭）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>是否在 ProcessCore() 中生成 VisibleMask。默认 false。</summary>
    public bool EnableVisibleMask { get; set; }

    /// <summary>Shadow 模式：运行 Visible-aware 搜索但不注入候选列表，仅写诊断。</summary>
    public bool EnableVisibleAwareShadow { get; set; }

    /// <summary>将 Visible-aware 候选注入 candidates 列表，参与统一排序和验证。Phase 4 起默认启用。</summary>
    public bool EnableVisibleAwareInjection { get; set; } = true;

    /// <summary>允许 Visible-aware 强结果提前跳过 ORB/Pyramid/Global。Phase 5 才启用。</summary>
    public bool EnableVisibleAwareEarlyExit { get; set; } = true;

    // Visible-aware 搜索参数
    public int VisibleAwareSearchBudgetMilliseconds { get; set; } = 150;
    public int VisibleAwareCoarseDownsample { get; set; } = 4;
    public int VisibleAwareTopK { get; set; } = 5;
    public double VisibleAwareMinimumVisibleFraction { get; set; } = 0.05;
    public int VisibleAwareMinimumVisibleStructurePixels { get; set; } = 50;
    public int SafeVisibleMaskErodePixels { get; set; } = 1;

    // VisibleMask 生成阈值
    public int VisibleVMin { get; set; } = 42;
    public int VisibleSMin { get; set; } = 14;
    public int VisibleHighlightVMin { get; set; } = 80;

    // 提前终止阈值（0 = 禁用）
    public double VisibleAwareEarlyTerminationMaxCompositeCost { get; set; } = 0.55d;

    // ═══════════════════════════════════════════════════════════════
    // 快速粗搜索（Fast Coarse Alignment）— 第一阶段实验
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启用快速粗搜索实验路径。默认 false。</summary>
    public bool EnableFastAlignment { get; set; } = true;

    /// <summary>快速路径失败时回退到现有 ORB 主链路。默认 true。</summary>
    public bool FastFallbackToLegacy { get; set; } = true;

    /// <summary>Shadow 模式：同时执行 Fast + Legacy，对比结果但只返回 Legacy。</summary>
    public bool FastAlignmentShadowMode { get; set; }

    /// <summary>粗搜索降采样因子 (2/4/8)。默认 2。</summary>
    public int FastCoarseDownsampleFactor { get; set; } = 2;

    /// <summary>粗搜索 NMS 提取的 Top-K 峰值数。默认 5。</summary>
    public int FastCoarseTopK { get; set; } = 5;

    /// <summary>NMS 抑制半径（降采样像素空间）。默认 12。</summary>
    public int FastCoarseNmsRadius { get; set; } = 12;

    /// <summary>粗搜索目标最大边长像素。默认 120。</summary>
    public int FastCoarseMaxDimension { get; set; } = 200;

    /// <summary>
    /// 禁用单假设 scale 早停：即使首个 scale 假设产生足够好的候选，
    /// 也继续搜索全部 scale 假设。供 seed scale 可能错误的恢复路径使用。
    /// </summary>
    public bool DisableScaleEarlyTermination { get; set; }

    public MapStructureRegistrationTuning Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        AuxiliaryAnchorMode = AuxiliaryAnchorMode,
        ReusePreviousAlignmentResult = ReusePreviousAlignmentResult,
        MaximumAuxiliaryTemplates = MaximumAuxiliaryTemplates,
        AuxiliaryDirectLockConfidence = AuxiliaryDirectLockConfidence,
        StructureFallbackBudgetMilliseconds =
            StructureFallbackBudgetMilliseconds,
        PreviousAlignmentSearchRadiusPixels =
            PreviousAlignmentSearchRadiusPixels,
        TrackingSearchRadiusPixels = TrackingSearchRadiusPixels,
        TrackingScaleSearchRadius = TrackingScaleSearchRadius,
        EarlyTerminationScoreThreshold = EarlyTerminationScoreThreshold,
        SkipEccScoreThreshold = SkipEccScoreThreshold,
        MinimumEdgePixels = MinimumEdgePixels,
        MinimumSpanPixels = MinimumSpanPixels,
        MinimumConsistentPartitions = MinimumConsistentPartitions,
        TopCandidateCount = TopCandidateCount,
        MaximumChamferPixels = MaximumChamferPixels,
        RestrictedSearchMaximumChamferPixels =
            RestrictedSearchMaximumChamferPixels,
        MinimumEdgeCoverage = MinimumEdgeCoverage,
        MinimumOccupancyCoverage = MinimumOccupancyCoverage,
        MinimumCandidateMargin = MinimumCandidateMargin,
        LocalSearchRadiusRatio = LocalSearchRadiusRatio,
        ScaleSearchRadius = ScaleSearchRadius,
        ScaleSearchStep = ScaleSearchStep,
        EdgeDistanceTolerancePixels = EdgeDistanceTolerancePixels,
        DistanceClipPixels = DistanceClipPixels,
        EnableDebugOutput = EnableDebugOutput,
        EnableEccRefinement = EnableEccRefinement,
        EnableFeatureVoting = EnableFeatureVoting,
        MaximumTranslationCandidates = MaximumTranslationCandidates,
        FeatureRatioThreshold = FeatureRatioThreshold,
        FeatureInlierTolerancePixels = FeatureInlierTolerancePixels,
        MaximumPlayerPriorDistanceRatio = MaximumPlayerPriorDistanceRatio,
        MapViewportEdgeMargin = MapViewportEdgeMargin,
        // Visible-aware
        EnableVisibleMask = EnableVisibleMask,
        EnableVisibleAwareShadow = EnableVisibleAwareShadow,
        EnableVisibleAwareInjection = EnableVisibleAwareInjection,
        EnableVisibleAwareEarlyExit = EnableVisibleAwareEarlyExit,
        VisibleAwareSearchBudgetMilliseconds = VisibleAwareSearchBudgetMilliseconds,
        VisibleAwareCoarseDownsample = VisibleAwareCoarseDownsample,
        VisibleAwareTopK = VisibleAwareTopK,
        VisibleAwareMinimumVisibleFraction = VisibleAwareMinimumVisibleFraction,
        VisibleAwareMinimumVisibleStructurePixels = VisibleAwareMinimumVisibleStructurePixels,
        SafeVisibleMaskErodePixels = SafeVisibleMaskErodePixels,
        VisibleVMin = VisibleVMin,
        VisibleSMin = VisibleSMin,
        VisibleHighlightVMin = VisibleHighlightVMin,
        VisibleAwareEarlyTerminationMaxCompositeCost = VisibleAwareEarlyTerminationMaxCompositeCost,
        // Fast alignment
        EnableFastAlignment = EnableFastAlignment,
        FastFallbackToLegacy = FastFallbackToLegacy,
        FastAlignmentShadowMode = FastAlignmentShadowMode,
        FastCoarseDownsampleFactor = FastCoarseDownsampleFactor,
        FastCoarseTopK = FastCoarseTopK,
        FastCoarseNmsRadius = FastCoarseNmsRadius,
        FastCoarseMaxDimension = FastCoarseMaxDimension,
        DisableScaleEarlyTermination = DisableScaleEarlyTermination
    };

    public void Normalize()
    {
        if (SchemaVersion < 1)
        {
            EnableEccRefinement = false;
            EnableDebugOutput = false;
        }
        if (SchemaVersion < 2)
            ReusePreviousAlignmentResult = true;
        if (SchemaVersion < 3)
            EnableFeatureVoting = true;
        if (SchemaVersion < 4)
            UseAuxiliaryAnchorRecognition = true;
        if (SchemaVersion < 5)
            AuxiliaryDirectLockConfidence = 0.82d;
        if (SchemaVersion < 6)
        {
            EnableVisibleAwareEarlyExit = true;
            VisibleAwareEarlyTerminationMaxCompositeCost = 0.55d;
        }
        if (SchemaVersion < 7)
        {
            // The former Boolean either disabled useful disambiguation or ran
            // the comparatively expensive anchor pass on every alignment.
            // Existing caches remain trusted; only the runtime policy changes.
            AuxiliaryAnchorMode =
                MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly;
        }
        SchemaVersion = CurrentSchemaVersion;
        if (!Enum.IsDefined(AuxiliaryAnchorMode))
        {
            AuxiliaryAnchorMode =
                MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly;
        }
        MaximumAuxiliaryTemplates = Math.Clamp(
            MaximumAuxiliaryTemplates,
            1,
            8);
        StructureFallbackBudgetMilliseconds = Math.Clamp(
            StructureFallbackBudgetMilliseconds,
            250,
            5000);
        PreviousAlignmentSearchRadiusPixels = Math.Clamp(
            PreviousAlignmentSearchRadiusPixels,
            8,
            1000);
        TrackingSearchRadiusPixels = Math.Clamp(
            TrackingSearchRadiusPixels,
            8,
            500);
        TrackingScaleSearchRadius = Finite(
            TrackingScaleSearchRadius,
            0.005d,
            0d,
            0.01d);
        EarlyTerminationScoreThreshold = Finite(
            EarlyTerminationScoreThreshold,
            0.55d,
            0.30d,
            0.80d);
        SkipEccScoreThreshold = Finite(
            SkipEccScoreThreshold,
            8d,
            2d,
            20d);
        MinimumEdgePixels = Math.Clamp(MinimumEdgePixels, 20, 10000);
        MinimumSpanPixels = Math.Clamp(MinimumSpanPixels, 8, 500);
        MinimumConsistentPartitions = Math.Clamp(MinimumConsistentPartitions, 1, 4);
        TopCandidateCount = Math.Clamp(TopCandidateCount, 2, 20);
        AuxiliaryDirectLockConfidence = Finite(
            AuxiliaryDirectLockConfidence,
            0.82d,
            0.65d,
            0.95d);
        MaximumChamferPixels = Finite(MaximumChamferPixels, 3.2d, 0.5d, 20d);
        RestrictedSearchMaximumChamferPixels = Finite(
            RestrictedSearchMaximumChamferPixels, 3.0d, 0.5d, 20d);
        MinimumEdgeCoverage = Finite(MinimumEdgeCoverage, 0.40d, 0.1d, 0.98d);
        MinimumOccupancyCoverage = Finite(MinimumOccupancyCoverage, 0.42d, 0.1d, 0.98d);
        MinimumCandidateMargin = Finite(MinimumCandidateMargin, 0.04d, 0.01d, 0.8d);
        LocalSearchRadiusRatio = Finite(LocalSearchRadiusRatio, 0.20d, 0.02d, 1d);
        // Recovery policy deliberately uses 0.15 initially and 0.30 as the
        // final uncalibrated fallback.  Clamping here to 0.05 silently made
        // both runtime stages execute the same narrow search.
        ScaleSearchRadius = Finite(ScaleSearchRadius, 0.02d, 0d, 0.30d);
        ScaleSearchStep = Finite(ScaleSearchStep, 0.01d, 0.0025d, 0.025d);
        EdgeDistanceTolerancePixels = Finite(
            EdgeDistanceTolerancePixels,
            2.25d,
            0.5d,
            8d);
        DistanceClipPixels = Finite(DistanceClipPixels, 12d, 3d, 50d);
        MaximumTranslationCandidates = Math.Clamp(
            MaximumTranslationCandidates,
            2,
            10);
        FeatureRatioThreshold = Finite(
            FeatureRatioThreshold,
            0.64d,
            0.50d,
            0.95d);
        FeatureInlierTolerancePixels = Finite(
            FeatureInlierTolerancePixels,
            6d,
            1d,
            30d);
        MaximumPlayerPriorDistanceRatio = Finite(
            MaximumPlayerPriorDistanceRatio,
            0.45d,
            0.10d,
            1d);
        MapViewportEdgeMargin = Finite(
            MapViewportEdgeMargin,
            0.20d,
            0d,
            0.30d);
        // Visible-aware: 布尔字段不需要钳制
        VisibleAwareSearchBudgetMilliseconds = Math.Clamp(
            VisibleAwareSearchBudgetMilliseconds, 20, 2000);
        VisibleAwareCoarseDownsample = Math.Clamp(
            VisibleAwareCoarseDownsample, 1, 8);
        VisibleAwareTopK = Math.Clamp(VisibleAwareTopK, 1, 20);
        VisibleAwareMinimumVisibleFraction = Finite(
            VisibleAwareMinimumVisibleFraction, 0.05d, 0.01d, 1d);
        VisibleAwareMinimumVisibleStructurePixels = Math.Clamp(
            VisibleAwareMinimumVisibleStructurePixels, 10, 10000);
        SafeVisibleMaskErodePixels = Math.Clamp(
            SafeVisibleMaskErodePixels, 0, 3);
        VisibleVMin = Math.Clamp(VisibleVMin, 10, 120);
        VisibleSMin = Math.Clamp(VisibleSMin, 5, 80);
        VisibleHighlightVMin = Math.Clamp(VisibleHighlightVMin, 40, 160);
        VisibleAwareEarlyTerminationMaxCompositeCost = double.IsFinite(
            VisibleAwareEarlyTerminationMaxCompositeCost)
            ? Math.Max(0d, VisibleAwareEarlyTerminationMaxCompositeCost)
            : 0d;
        // Fast alignment
        FastCoarseDownsampleFactor = Math.Clamp(
            FastCoarseDownsampleFactor, 2, 8);
        FastCoarseTopK = Math.Clamp(FastCoarseTopK, 3, 20);
        FastCoarseNmsRadius = Math.Clamp(FastCoarseNmsRadius, 4, 40);
        FastCoarseMaxDimension = Math.Clamp(FastCoarseMaxDimension, 40, 400);
    }

    private static double Finite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public bool ShouldUseAuxiliaryAnchors(bool isAmbiguous) =>
        AuxiliaryAnchorMode switch
        {
            MapAuxiliaryAnchorRecognitionMode.Always => true,
            MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly => isAmbiguous,
            _ => false
        };
}
