using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public enum MapAuxiliaryAnchorRecognitionMode
{
    Off = 0,
    AmbiguityOnly = 1,
    Always = 2
}

public enum MapStructureEdgeComposition
{
    CannyOnly = 0,
    GradientAndCanny = 1
}

public sealed class MapStructureGenerationTuning
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public MapStructureEdgeComposition ReferenceEdgeComposition { get; set; } =
        MapStructureEdgeComposition.GradientAndCanny;
    public MapStructureEdgeComposition LiveEdgeComposition { get; set; } =
        MapStructureEdgeComposition.GradientAndCanny;
    public double CannyLowThreshold { get; set; } = 35d;
    public double CannyHighThreshold { get; set; } = 110d;
    public int StructureCloseKernelSize { get; set; } = 5;
    public int StructureOpenKernelSize { get; set; } = 3;
    public int EdgeClosingKernelSize { get; set; } = 3;
    public int EdgeClosingIterations { get; set; }
    public int LiveGradientSupportRadiusPixels { get; set; } = 4;
    public int DominantClusterAttachmentDistancePixels { get; set; }
    public double DominantClusterMinimumAttachedAreaRatio { get; set; } = 0.001d;
    public int MinimumEdgeComponentAreaPixels { get; set; } = 8;

    [JsonIgnore]
    public string CacheFingerprint
    {
        get
        {
            var normalized = Clone();
            normalized.Normalize();
            var canonical = string.Join(
                "|",
                normalized.SchemaVersion,
                (int)normalized.ReferenceEdgeComposition,
                (int)normalized.LiveEdgeComposition,
                normalized.CannyLowThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                normalized.CannyHighThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                normalized.StructureCloseKernelSize,
                normalized.StructureOpenKernelSize,
                normalized.EdgeClosingKernelSize,
                normalized.EdgeClosingIterations,
                normalized.LiveGradientSupportRadiusPixels,
                normalized.DominantClusterAttachmentDistancePixels,
                normalized.DominantClusterMinimumAttachedAreaRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                normalized.MinimumEdgeComponentAreaPixels);
            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant()[..16];
        }
    }

    public MapStructureGenerationTuning Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        ReferenceEdgeComposition = ReferenceEdgeComposition,
        LiveEdgeComposition = LiveEdgeComposition,
        CannyLowThreshold = CannyLowThreshold,
        CannyHighThreshold = CannyHighThreshold,
        StructureCloseKernelSize = StructureCloseKernelSize,
        StructureOpenKernelSize = StructureOpenKernelSize,
        EdgeClosingKernelSize = EdgeClosingKernelSize,
        EdgeClosingIterations = EdgeClosingIterations,
        LiveGradientSupportRadiusPixels = LiveGradientSupportRadiusPixels,
        DominantClusterAttachmentDistancePixels =
            DominantClusterAttachmentDistancePixels,
        DominantClusterMinimumAttachedAreaRatio =
            DominantClusterMinimumAttachedAreaRatio,
        MinimumEdgeComponentAreaPixels = MinimumEdgeComponentAreaPixels
    };

    public static MapStructureGenerationTuning CreateLegacyBaseline() => new()
    {
        LiveEdgeComposition = MapStructureEdgeComposition.CannyOnly
    };

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        if (!Enum.IsDefined(ReferenceEdgeComposition))
            ReferenceEdgeComposition = MapStructureEdgeComposition.GradientAndCanny;
        if (!Enum.IsDefined(LiveEdgeComposition))
            LiveEdgeComposition = MapStructureEdgeComposition.GradientAndCanny;
        CannyLowThreshold = Finite(CannyLowThreshold, 35d, 0d, 254d);
        CannyHighThreshold = Finite(CannyHighThreshold, 110d, 1d, 255d);
        if (CannyHighThreshold <= CannyLowThreshold)
            CannyHighThreshold = Math.Min(255d, CannyLowThreshold + 1d);
        StructureCloseKernelSize = OddKernel(
            StructureCloseKernelSize, 5, 1, 31);
        StructureOpenKernelSize = OddKernel(
            StructureOpenKernelSize, 3, 1, 31);
        EdgeClosingKernelSize = OddKernel(
            EdgeClosingKernelSize, 3, 1, 31);
        EdgeClosingIterations = Math.Clamp(EdgeClosingIterations, 0, 4);
        LiveGradientSupportRadiusPixels = Math.Clamp(
            LiveGradientSupportRadiusPixels, 0, 32);
        DominantClusterAttachmentDistancePixels = Math.Clamp(
            DominantClusterAttachmentDistancePixels, 0, 96);
        DominantClusterMinimumAttachedAreaRatio = Finite(
            DominantClusterMinimumAttachedAreaRatio,
            0.001d,
            0.00001d,
            0.10d);
        MinimumEdgeComponentAreaPixels = Math.Clamp(
            MinimumEdgeComponentAreaPixels, 1, 10000);
    }

    private static int OddKernel(
        int value,
        int fallback,
        int minimum,
        int maximum)
    {
        value = Math.Clamp(value, minimum, maximum);
        if ((value & 1) == 0)
            value = value < maximum ? value + 1 : value - 1;
        return value > 0 ? value : fallback;
    }

    private static double Finite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed partial class MapStructureRegistrationTuning
{
    public const int CurrentSchemaVersion = 9;
    public const double LockedMaximumChamferPixels = 3.0d;

    public int SchemaVersion { get; set; }
    [JsonIgnore]
    public MapAlignmentChannel Channel { get; set; } = MapAlignmentChannel.Standard;
    public MapAuxiliaryAnchorRecognitionMode AuxiliaryAnchorMode { get; set; } =
        MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly;

    [JsonIgnore]
    public string CacheFingerprint
    {
        get
        {
            var normalized = Clone();
            normalized.Normalize();
            var canonical = string.Concat(
                normalized.Channel,
                "|",
                JsonSerializer.Serialize(normalized));
            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant()[..16];
        }
    }

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
    /// <summary>When false, the value above is diagnostic only.</summary>
    public bool EnforceTimeBudget { get; set; } = true;
    public int PreviousAlignmentSearchRadiusPixels { get; set; } = 96;
    public int TrackingSearchRadiusPixels { get; set; } = 48;
    public double TrackingScaleSearchRadius { get; set; } = 0.005d;
    public double EarlyTerminationScoreThreshold { get; set; } = 0.55d;
    public double SkipEccScoreThreshold { get; set; } = 8d;
    public int MinimumEdgePixels { get; set; } = 90;
    public int MinimumSpanPixels { get; set; } = 28;
    public int MinimumConsistentPartitions { get; set; } = 2;
    public int TopCandidateCount { get; set; } = 6;
    /// <summary>
    /// Global Chamfer hard limit. The setter remains for persisted-settings
    /// compatibility, but every attempted override is deliberately ignored.
    /// </summary>
    public double MaximumChamferPixels
    {
        get => LockedMaximumChamferPixels;
        set { }
    }
    /// <summary>
    /// 受限搜索(RestrictSearchToLockedTransform=true)专用的独立 chamfer 上限。
    /// 不受分辨率档 TOML 的 MaximumChamferPixels 放宽影响——受限窗口很小,
    /// 真位置在窗口内时 chamfer 必然低,假候选(部分重叠)chamfer 显著偏高。
    /// </summary>
    public double RestrictedSearchMaximumChamferPixels
    {
        get => LockedMaximumChamferPixels;
        set { }
    }
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
    public MapStructureGenerationTuning Generation { get; set; } = new();

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
    internal VisibleAwareCorrelationMode VisibleAwareCorrelationMode { get; set; } =
        VisibleAwareCorrelationMode.CoarseMat;

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
    public int FastCoarseMinimumTemplateDimension { get; set; } = 12;
    public double MinimumUsableScale { get; set; } = 0.05d;

    // Low-structure route budget and quality policy. These fields are kept on
    // the per-call tuning copy so the standard channel remains unchanged.
    public int LowStructureWarmPathBudgetMilliseconds { get; set; } = 300;
    public int LowStructureColdPathBudgetMilliseconds { get; set; } = 700;
    public int LowStructureEndToEndBudgetMilliseconds { get; set; } = 1200;
    public int LowStructureMaximumScalesPerFrame { get; set; } = 3;
    public int LowStructureTranslationTopK { get; set; } = 2;
    public int LowStructureReadinessFrameCount { get; set; } = 3;
    public double LowStructureScaleConsistencyTolerance { get; set; } = 0.015d;
    public int LowStructureCacheConfirmationCount { get; set; } = 2;
    public double LowStructureMinimumReferenceCoverage { get; set; } = 0.50d;
    public double LowStructureMinimumProjectionCorrelation { get; set; } = 0.75d;
    public bool LowStructureEnableFeatureScaleEstimate { get; set; }

    /// <summary>
    /// 禁用单假设 scale 早停：即使首个 scale 假设产生足够好的候选，
    /// 也继续搜索全部 scale 假设。供 seed scale 可能错误的恢复路径使用。
    /// </summary>
    public bool DisableScaleEarlyTermination { get; set; }
    public double LowStructureMinimumScale { get; set; } = 0.40d;
    public double LowStructureMaximumScale { get; set; } = 1.60d;
    public int LowStructureScaleHypothesisCount { get; set; } = 13;
    public double EdgeCoverageWeight { get; set; } = 4d;
    public double ChamferWeight { get; set; } = 1d;
    public double OccupancyCoverageWeight { get; set; } = 2d;
    public double ReferenceCoverageWeight { get; set; } = 4d;
    public double PartitionPenaltyWeight { get; set; } = 0.75d;
    public double PriorDisagreementWeight { get; set; } = 0.75d;
    public int MinimumEdgesPerPartition { get; set; } = 12;
    public double MinimumPartitionCoverage { get; set; } = 0.45d;
    public double BoundsPenalty { get; set; } = 100d;
    public double MaximumScaleChangeRatio { get; set; } = 0.15d;
    public double MinimumPriorAgreement { get; set; } = 0.05d;
    public double GlobalSearchMarginMultiplier { get; set; } = 1.25d;
    public double MarginNormalizationFloor { get; set; } = 0.01d;
    public double ScaleDuplicateTolerance { get; set; } = 0.000001d;
    public double SpatialDuplicateTolerance { get; set; } = 2d;
    public double CandidateDuplicateRadius { get; set; } = 1d;
    public double RefinementWorsenTolerance { get; set; } = 0.001d;

    public MapStructureRegistrationTuning Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        AuxiliaryAnchorMode = AuxiliaryAnchorMode,
        ReusePreviousAlignmentResult = ReusePreviousAlignmentResult,
        MaximumAuxiliaryTemplates = MaximumAuxiliaryTemplates,
        AuxiliaryDirectLockConfidence = AuxiliaryDirectLockConfidence,
        StructureFallbackBudgetMilliseconds =
            StructureFallbackBudgetMilliseconds,
        EnforceTimeBudget = EnforceTimeBudget,
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
        Generation = Generation?.Clone() ?? new(),
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
        VisibleAwareCorrelationMode = VisibleAwareCorrelationMode,
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
        FastCoarseMinimumTemplateDimension =
            FastCoarseMinimumTemplateDimension,
        MinimumUsableScale = MinimumUsableScale,
        LowStructureWarmPathBudgetMilliseconds = LowStructureWarmPathBudgetMilliseconds,
        LowStructureColdPathBudgetMilliseconds = LowStructureColdPathBudgetMilliseconds,
        LowStructureEndToEndBudgetMilliseconds = LowStructureEndToEndBudgetMilliseconds,
        LowStructureMaximumScalesPerFrame = LowStructureMaximumScalesPerFrame,
        LowStructureTranslationTopK = LowStructureTranslationTopK,
        LowStructureReadinessFrameCount = LowStructureReadinessFrameCount,
        LowStructureScaleConsistencyTolerance = LowStructureScaleConsistencyTolerance,
        LowStructureCacheConfirmationCount = LowStructureCacheConfirmationCount,
        LowStructureMinimumReferenceCoverage = LowStructureMinimumReferenceCoverage,
        LowStructureMinimumProjectionCorrelation = LowStructureMinimumProjectionCorrelation,
        LowStructureEnableFeatureScaleEstimate = LowStructureEnableFeatureScaleEstimate,
        DisableScaleEarlyTermination = DisableScaleEarlyTermination,
        LowStructureMinimumScale = LowStructureMinimumScale,
        LowStructureMaximumScale = LowStructureMaximumScale,
        LowStructureScaleHypothesisCount = LowStructureScaleHypothesisCount,
        EdgeCoverageWeight = EdgeCoverageWeight,
        ChamferWeight = ChamferWeight,
        OccupancyCoverageWeight = OccupancyCoverageWeight,
        ReferenceCoverageWeight = ReferenceCoverageWeight,
        PartitionPenaltyWeight = PartitionPenaltyWeight,
        PriorDisagreementWeight = PriorDisagreementWeight,
        MinimumEdgesPerPartition = MinimumEdgesPerPartition,
        MinimumPartitionCoverage = MinimumPartitionCoverage,
        BoundsPenalty = BoundsPenalty,
        MaximumScaleChangeRatio = MaximumScaleChangeRatio,
        MinimumPriorAgreement = MinimumPriorAgreement,
        GlobalSearchMarginMultiplier = GlobalSearchMarginMultiplier,
        MarginNormalizationFloor = MarginNormalizationFloor,
        ScaleDuplicateTolerance = ScaleDuplicateTolerance,
        SpatialDuplicateTolerance = SpatialDuplicateTolerance,
        CandidateDuplicateRadius = CandidateDuplicateRadius,
        RefinementWorsenTolerance = RefinementWorsenTolerance,
        Channel = Channel
    };
}
/*
 * 文件职责：MapStructureRegistrationModels.Tuning。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
