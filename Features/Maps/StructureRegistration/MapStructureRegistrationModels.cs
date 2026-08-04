using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public enum MapStructureRejectionReason
{
    None,
    InvalidInput,
    UnsupportedAlignmentMode,
    InvalidLockedScale,
    InsufficientStructure,
    QueryLargerThanReference,
    NoCandidate,
    WeakAbsoluteScore,
    AmbiguousCandidates,
    InconsistentStructure,
    ScaleChangeTooLarge,
    RefinementFailed,
    OutsideValidBounds,
    PlayerPriorMismatch,
    NativeScaleChanged,
    AnchorTransformConflict,
    TimeBudgetExceeded
}

public enum MapStructureEvidenceDisposition
{
    None,
    Supportive,
    Inconclusive,
    Contradictory,
    SystemError
}

public static class MapStructureRejectionReasonExtensions
{
    public static string ToDisplayText(this MapStructureRejectionReason reason) => reason switch
    {
        MapStructureRejectionReason.InvalidInput => "结构配准输入无效",
        MapStructureRejectionReason.UnsupportedAlignmentMode => "结构配准只支持等比缩放",
        MapStructureRejectionReason.InvalidLockedScale => "历史等比缩放无效，需要双门重新锁定",
        MapStructureRejectionReason.InsufficientStructure => "当前已探索地图结构过少或分布过于单一",
        MapStructureRejectionReason.QueryLargerThanReference => "当前结构范围大于参考地图，无法安全搜索",
        MapStructureRejectionReason.NoCandidate => "没有找到可用的结构候选",
        MapStructureRejectionReason.WeakAbsoluteScore => "最佳候选与墙体结构的绝对贴合度不足",
        MapStructureRejectionReason.AmbiguousCandidates => "存在多个近似房间或走廊，候选不唯一",
        MapStructureRejectionReason.InconsistentStructure => "候选只在局部区域吻合，分区证据不一致",
        MapStructureRejectionReason.ScaleChangeTooLarge => "疑似发生了超过安全范围的地图缩放",
        MapStructureRejectionReason.RefinementFailed => "局部精修未能改善结构贴合度",
        MapStructureRejectionReason.OutsideValidBounds => "候选视口超出地图有效边界",
        MapStructureRejectionReason.PlayerPriorMismatch => "候选与玩家位置先验明显冲突",
        MapStructureRejectionReason.NativeScaleChanged => "原生地图缩放与固定标定不一致",
        MapStructureRejectionReason.AnchorTransformConflict => "结构精修与锚点变换明显冲突",
        MapStructureRejectionReason.TimeBudgetExceeded => "结构配准超过时间预算",
        _ => string.Empty
    };

    public static MapStructureEvidenceDisposition ToDisposition(
        this MapStructureRejectionReason reason,
        bool accepted = false) =>
        accepted && reason == MapStructureRejectionReason.None
            ? MapStructureEvidenceDisposition.Supportive
            : reason switch
            {
                MapStructureRejectionReason.None =>
                    MapStructureEvidenceDisposition.None,
                MapStructureRejectionReason.InsufficientStructure
                    or MapStructureRejectionReason.QueryLargerThanReference
                    or MapStructureRejectionReason.NoCandidate
                    or MapStructureRejectionReason.WeakAbsoluteScore
                    or MapStructureRejectionReason.AmbiguousCandidates
                    or MapStructureRejectionReason.InconsistentStructure
                    or MapStructureRejectionReason.RefinementFailed
                    or MapStructureRejectionReason.TimeBudgetExceeded =>
                    MapStructureEvidenceDisposition.Inconclusive,
                MapStructureRejectionReason.ScaleChangeTooLarge
                    or MapStructureRejectionReason.OutsideValidBounds
                    or MapStructureRejectionReason.PlayerPriorMismatch
                    or MapStructureRejectionReason.NativeScaleChanged
                    or MapStructureRejectionReason.AnchorTransformConflict =>
                    MapStructureEvidenceDisposition.Contradictory,
                _ => MapStructureEvidenceDisposition.SystemError
            };

    /// <summary>
    /// Classifies evidence for invalidating an already trusted alignment.
    /// Candidate-local failures (bounds, priors, ambiguity, refinement, and
    /// anchor disagreement) still reject that candidate, but do not prove the
    /// currently rendered lock is wrong. Only an explicit measured native
    /// scale change is contradictory lock evidence.
    /// </summary>
    public static MapStructureEvidenceDisposition ToContinuousLockDisposition(
        this MapStructureRejectionReason reason) => reason switch
        {
            MapStructureRejectionReason.ScaleChangeTooLarge
                or MapStructureRejectionReason.NativeScaleChanged =>
                MapStructureEvidenceDisposition.Contradictory,
            MapStructureRejectionReason.InvalidInput
                or MapStructureRejectionReason.UnsupportedAlignmentMode
                or MapStructureRejectionReason.InvalidLockedScale =>
                MapStructureEvidenceDisposition.SystemError,
            MapStructureRejectionReason.None =>
                MapStructureEvidenceDisposition.None,
            _ => MapStructureEvidenceDisposition.Inconclusive
        };
}

public sealed class MapStructureRegistrationTuning
{
    public const int CurrentSchemaVersion = 6;

    public int SchemaVersion { get; set; }
    public bool UseAuxiliaryAnchorRecognition { get; set; } = true;
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
    public double MinimumEdgeCoverage { get; set; } = 0.55d;
    public double MinimumOccupancyCoverage { get; set; } = 0.42d;
    public double MinimumCandidateMargin { get; set; } = 0.08d;
    public double LocalSearchRadiusRatio { get; set; } = 0.20d;
    public double ScaleSearchRadius { get; set; } = 0.02d;
    public double ScaleSearchStep { get; set; } = 0.01d;
    public double EdgeDistanceTolerancePixels { get; set; } = 2.25d;
    public double DistanceClipPixels { get; set; } = 12d;
    public bool EnableDebugOutput { get; set; }
    public bool EnableEccRefinement { get; set; } = true;
    public bool EnableFeatureVoting { get; set; } = true;
    public int MaximumTranslationCandidates { get; set; } = 5;
    public double FeatureRatioThreshold { get; set; } = 0.78d;
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
    public bool EnableVisibleAwareEarlyExit { get; set; }

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
    public double VisibleAwareEarlyTerminationMaxCompositeCost { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // 快速粗搜索（Fast Coarse Alignment）— 第一阶段实验
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启用快速粗搜索实验路径。默认 false。</summary>
    public bool EnableFastAlignment { get; set; }

    /// <summary>快速路径失败时回退到现有 ORB 主链路。默认 true。</summary>
    public bool FastFallbackToLegacy { get; set; } = true;

    /// <summary>Shadow 模式：同时执行 Fast + Legacy，对比结果但只返回 Legacy。</summary>
    public bool FastAlignmentShadowMode { get; set; }

    /// <summary>粗搜索降采样因子 (2/4/8)。默认 4。</summary>
    public int FastCoarseDownsampleFactor { get; set; } = 4;

    /// <summary>粗搜索 NMS 提取的 Top-K 峰值数。默认 5。</summary>
    public int FastCoarseTopK { get; set; } = 5;

    /// <summary>NMS 抑制半径（降采样像素空间）。默认 12。</summary>
    public int FastCoarseNmsRadius { get; set; } = 12;

    /// <summary>粗搜索目标最大边长像素。默认 120。</summary>
    public int FastCoarseMaxDimension { get; set; } = 120;

    public MapStructureRegistrationTuning Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        UseAuxiliaryAnchorRecognition = UseAuxiliaryAnchorRecognition,
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
        FastCoarseMaxDimension = FastCoarseMaxDimension
    };

    public void Normalize()
    {
        if (SchemaVersion < 1)
        {
            EnableEccRefinement = true;
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
        SchemaVersion = CurrentSchemaVersion;
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
        MinimumEdgeCoverage = Finite(MinimumEdgeCoverage, 0.55d, 0.1d, 0.98d);
        MinimumOccupancyCoverage = Finite(MinimumOccupancyCoverage, 0.42d, 0.1d, 0.98d);
        MinimumCandidateMargin = Finite(MinimumCandidateMargin, 0.08d, 0.01d, 0.8d);
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
            0.78d,
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
}

public sealed class MapStructureRegistrationRequest
{
    public Mat ReferenceImage { get; init; } = new();
    public Mat LiveRoi { get; init; } = new();
    public MapScreenRect ViewportBounds { get; init; }
    public MapOverlayTransform LockedTransform { get; init; } = new();
    public MapStructureRegistrationTuning Tuning { get; init; } = new();
    public bool AllowScaleSearch { get; init; }
    public bool RestrictSearchToLockedTransform { get; init; }
    public bool TrackingMode { get; init; }
    public bool ForceBestCandidate { get; init; }
    public double FixedRotationDegrees { get; init; }
    public MapReferenceBounds? ValidMapBounds { get; init; }
    public MapViewportOrigin? PredictedViewportOrigin { get; init; }
    public MapReferencePoint? PlayerPrior { get; init; }
    public IReadOnlyList<MapSimilarityTransform> CandidateHistory { get; init; } = [];
    public IReadOnlyList<NormalizedRectangle> LiveIgnoreRegions { get; init; } = [];
    public IReadOnlyList<Rect> DynamicIgnoreRegions { get; init; } = [];
    public string? DebugOutputDirectory { get; init; }
    public MapStructureFeatures? PreparedReference { get; init; }
    public MapStructureFeatures? PreparedLive { get; init; }
    /// <summary>侧门扫描先验置信度（0表示无先验）。用于区分冷启动和跟踪模式。</summary>
    public double SideEntrancePrior { get; init; }
}

public sealed record MapStructureCandidate
{
    public double Scale { get; init; }
    public int ReferenceX { get; init; }
    public int ReferenceY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double ChamferPixels { get; init; }
    public double EdgeCoverage { get; init; }
    public double OccupancyCoverage { get; init; }
    public int ConsistentPartitions { get; init; }
    public bool UsedGlobalSearch { get; init; }
    public double CompositeCost { get; init; }
    public int FeatureInlierCount { get; init; }
    public double FeatureConsensus { get; init; }
    public double PriorAgreement { get; init; } = 1d;
    public bool IsWithinValidBounds { get; init; } = true;
    public bool EccConverged { get; init; }
    public double EccCorrelation { get; init; }
    public bool FromVisibleAware { get; init; }
    public double VisibleFraction { get; init; }
    public int VisibleStructurePixels { get; init; }
    public int VisibleEdgePixels { get; init; }
}

/// <summary>
/// Serializable, replayable explanation of the separated geometry, evidence,
/// and lock confidence scores.
/// </summary>
public sealed record MapStructureConfidenceBreakdown
{
    public double ChamferPixels { get; init; }
    public double ChamferQuality { get; init; }
    public double EdgeCoverage { get; init; }
    public double OccupancyCoverage { get; init; }
    public int ConsistentPartitions { get; init; }
    public double PartitionQuality { get; init; }
    public double StructureQuality { get; init; }
    public double? FeatureConsensus { get; init; }
    public double CandidateSeparation { get; init; }
    public double? RefinementQuality { get; init; }
    public double BoundsAndPrior { get; init; }
    public double GeometricFitQuality { get; init; }
    public double EvidenceConfidence { get; init; }
    /// <summary>
    /// Diagnostic score from the proposed geometry-dominant lock formula.
    /// It is intentionally not used for runtime lock gating because the
    /// existing thresholds were calibrated against <see cref="LockConfidence"/>.
    /// </summary>
    public double GeometricLockConfidence { get; init; }
    public double LockConfidence { get; init; }
    public string? LowEvidenceReason { get; init; }
    public string? HardGateFailure { get; init; }
    public double EffectiveWeight { get; init; }
    public double FinalScore { get; init; }
}

public static class MapStructureConfidenceCalculator
{
    public static MapStructureConfidenceBreakdown Calculate(
        MapStructureCandidate best,
        double candidateMargin,
        MapStructureRegistrationTuning tuning,
        MapStructureRejectionReason hardGateFailure =
            MapStructureRejectionReason.None,
        bool isTrackingMode = false,
        double sideEntrancePrior = 0d)
    {
        var chamferQuality = Math.Clamp(
            1d - (best.ChamferPixels / tuning.MaximumChamferPixels),
            0d,
            1d);
        var partitionQuality = Math.Clamp(best.ConsistentPartitions / 4d, 0d, 1d);
        var geometricFitQuality = Math.Clamp(
            (chamferQuality * 0.60d)
            + (best.EdgeCoverage * 0.40d),
            0d,
            1d);
        double? featureConsensus = best.FeatureInlierCount > 0
            ? best.FeatureConsensus
            : null;
        double? refinementQuality = best.EccConverged
            ? Math.Clamp(best.EccCorrelation, 0d, 1d)
            : null;
        var boundsAndPrior = best.IsWithinValidBounds
            ? best.PriorAgreement
            : 0d;
        var evidenceItems = new (double? Value, double Weight)[]
        {
            (best.OccupancyCoverage, 0.35d),
            (partitionQuality, 0.25d),
            (featureConsensus, 0.20d),
            (refinementQuality, 0.20d)
        };
        var availableEvidence = evidenceItems
            .Where(item => item.Value is { } value && double.IsFinite(value))
            .ToArray();
        var evidenceWeight = availableEvidence.Sum(item => item.Weight);
        var evidenceConfidence = evidenceWeight <= 0d
            ? 0d
            : Math.Clamp(
                availableEvidence.Sum(item =>
                    Math.Clamp(item.Value!.Value, 0d, 1d) * item.Weight)
                    / evidenceWeight,
                0d,
                1d);
        var geometricLockConfidence = Math.Clamp(
            (geometricFitQuality * 0.75d)
            + (boundsAndPrior * 0.15d)
            + (candidateMargin * 0.10d),
            0d,
            1d);

        // 追踪模式下(已知地图ID)降低chamfer权重，提高覆盖率权重：
        // 视口边缘裁剪、动态遮挡等会降低chamfer分数，但edge/occupancy
        // 覆盖率能更可靠地反映对齐质量。
        var structureQuality = isTrackingMode
            ? Math.Clamp(
                (chamferQuality * 0.15d)        // 35% → 15%
                + (best.EdgeCoverage * 0.35d)   // 30% → 35%
                + (best.OccupancyCoverage * 0.35d) // 20% → 35%
                + (partitionQuality * 0.15d),   // 保持 15%
                0d,
                1d)
            : Math.Clamp(
                (chamferQuality * 0.35d)
                + (best.EdgeCoverage * 0.30d)
                + (best.OccupancyCoverage * 0.20d)
                + (partitionQuality * 0.15d),
                0d,
                1d);

        // ORB voting is a proposal generator. A tiny accidental cluster is
        // not contradictory evidence and must not reduce an otherwise valid
        // geometric lock; only trusted consensus participates in the runtime
        // lock score. The raw value remains in EvidenceConfidence/diagnostics.
        var runtimeFeatureConsensus = featureConsensus
            is >= StructureRegistrationRules.MinimumTrustedFeatureConsensus
            ? featureConsensus
            : null;
        var runtimeEvidence = new MapRegistrationConfidenceEvidence
        {
            FeatureConsensus = runtimeFeatureConsensus,
            CandidateSeparation = candidateMargin,
            StructureQuality = structureQuality,
            RefinementQuality = refinementQuality,
            BoundsAndPrior = boundsAndPrior
        };
        var runtimeEffectiveWeight = 0.10d + 0.25d + 0.10d
            + (runtimeFeatureConsensus.HasValue ? 0.15d : 0d)
            + (refinementQuality.HasValue ? 0.10d : 0d);
        var lockConfidence = runtimeEvidence.Calculate();

        // 侧门扫描先验融合：地图ID已知，结构配准只需定位视口
        if (sideEntrancePrior > 0d)
        {
            var locationQuality = structureQuality;
            lockConfidence = MapAlignmentConfidence.ComputeSideEntranceStructureConfidence(
                sideEntrancePrior,
                locationQuality,
                candidateMargin,
                runtimeFeatureConsensus ?? -1d,
                refinementQuality ?? -1d);
        }

        var lowEvidenceReasons = new List<string>();
        if (best.OccupancyCoverage < tuning.MinimumOccupancyCoverage)
            lowEvidenceReasons.Add("OccupancyCoverageBelowMinimum");
        if (best.ConsistentPartitions < tuning.MinimumConsistentPartitions)
            lowEvidenceReasons.Add("InconsistentPartitions");
        if (featureConsensus is { } rawFeatureConsensus
            && rawFeatureConsensus
                < StructureRegistrationRules.MinimumTrustedFeatureConsensus)
        {
            lowEvidenceReasons.Add("WeakFeatureConsensus");
        }
        return new MapStructureConfidenceBreakdown
        {
            ChamferPixels = best.ChamferPixels,
            ChamferQuality = chamferQuality,
            EdgeCoverage = best.EdgeCoverage,
            OccupancyCoverage = best.OccupancyCoverage,
            ConsistentPartitions = best.ConsistentPartitions,
            PartitionQuality = partitionQuality,
            StructureQuality = structureQuality,
            FeatureConsensus = featureConsensus,
            CandidateSeparation = candidateMargin,
            RefinementQuality = refinementQuality,
            BoundsAndPrior = boundsAndPrior,
            GeometricFitQuality = geometricFitQuality,
            EvidenceConfidence = evidenceConfidence,
            GeometricLockConfidence = geometricLockConfidence,
            LockConfidence = lockConfidence,
            LowEvidenceReason = lowEvidenceReasons.Count == 0
                ? null
                : string.Join(",", lowEvidenceReasons),
            HardGateFailure = hardGateFailure == MapStructureRejectionReason.None
                ? null
                : hardGateFailure.ToString(),
            EffectiveWeight = runtimeEffectiveWeight,
            FinalScore = lockConfidence
        };
    }
}

public sealed class MapStructureRegistrationResult
{
    public bool Accepted { get; init; }
    public MapOverlayTransform? Transform { get; init; }
    public double Confidence { get; init; }
    public MapStructureConfidenceBreakdown? ConfidenceBreakdown { get; init; }
    public double BestScore { get; init; }
    public double SecondScore { get; init; }
    public double CandidateMargin { get; init; }
    public MapStructureRejectionReason RejectionReason { get; init; }
    public string FailureReason { get; init; } = string.Empty;
    public IReadOnlyList<MapStructureCandidate> Candidates { get; init; } = [];
    public double PreprocessMilliseconds { get; init; }
    public double SearchMilliseconds { get; init; }
    public double RefineMilliseconds { get; init; }
    public double DistanceMapMilliseconds { get; init; }
    public double QueryConstructionMilliseconds { get; init; }
    public double HistoryCandidateMilliseconds { get; init; }
    public double FeatureVotingMilliseconds { get; init; }
    public double PyramidSearchMilliseconds { get; init; }
    public double LocalTemplateSearchMilliseconds { get; init; }
    public double GlobalTemplateSearchMilliseconds { get; init; }
    public double CandidateRankingMilliseconds { get; init; }
    public string? DebugOutputDirectory { get; init; }
    public double LockedScale { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public int QueryEdgePixels { get; init; }
    public int QueryBoundsX { get; init; }
    public int QueryBoundsY { get; init; }
    public int QueryBoundsWidth { get; init; }
    public int QueryBoundsHeight { get; init; }
    public int ScaleHypothesisCount { get; init; }
    public int OversizedHypothesisCount { get; init; }
    public bool UsedRestrictedSearch { get; init; }
    public bool WasForcedBestCandidate { get; init; }
    public int FeatureMatchCount { get; init; }
    public int FeatureInlierCount { get; init; }
    public double FeatureConsensus { get; init; }
    public bool EccConverged { get; init; }
    public double EccCorrelation { get; init; }
    public double VisibleMaskMilliseconds { get; init; }
    public double VisibleFraction { get; init; }
    public int VisibleStructurePixels { get; init; }
    public int VisibleEdgePixels { get; init; }
    public double VisibleAwareSearchMilliseconds { get; init; }
    public int VisibleAwareCandidateCount { get; init; }
    public double VisibleAwareTopCost { get; init; }
    public double VisibleAwareTopMargin { get; init; }
    public bool VisibleAwareEarlyAccepted { get; init; }
    public string? VisibleAwareFallbackReason { get; init; }

    // Fast alignment diagnostics
    public bool UsedFastStrategy { get; init; }
    public double FastCoarseSearchMilliseconds { get; init; }
    public int FastCoarseCandidateCount { get; init; }

    public static MapStructureRegistrationResult Reject(
        MapStructureRejectionReason reason,
        string? detail = null,
        IReadOnlyList<MapStructureCandidate>? candidates = null,
        double preprocessMilliseconds = 0d,
        double searchMilliseconds = 0d,
        string? debugOutputDirectory = null,
        double lockedScale = 0d,
        int referenceWidth = 0,
        int referenceHeight = 0,
        int queryEdgePixels = 0,
        Rect? queryBounds = null,
        int scaleHypothesisCount = 0,
        int oversizedHypothesisCount = 0,
        bool usedRestrictedSearch = false,
        double visibleMaskMilliseconds = 0d,
        double visibleFraction = 0d,
        int visibleStructurePixels = 0,
        int visibleEdgePixels = 0,
        double visibleAwareSearchMilliseconds = 0d,
        int visibleAwareCandidateCount = 0,
        double visibleAwareTopCost = 0d,
        double visibleAwareTopMargin = 0d,
        bool visibleAwareEarlyAccepted = false,
        string? visibleAwareFallbackReason = null,
        double distanceMapMilliseconds = 0d,
        double queryConstructionMilliseconds = 0d,
        double historyCandidateMilliseconds = 0d,
        double featureVotingMilliseconds = 0d,
        double pyramidSearchMilliseconds = 0d,
        double localTemplateSearchMilliseconds = 0d,
        double globalTemplateSearchMilliseconds = 0d,
        double candidateRankingMilliseconds = 0d) =>
        new()
        {
            RejectionReason = reason,
            FailureReason = string.IsNullOrWhiteSpace(detail)
                ? reason.ToDisplayText()
                : detail,
            Candidates = candidates ?? [],
            PreprocessMilliseconds = preprocessMilliseconds,
            SearchMilliseconds = searchMilliseconds,
            DebugOutputDirectory = debugOutputDirectory,
            LockedScale = lockedScale,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            QueryEdgePixels = queryEdgePixels,
            QueryBoundsX = queryBounds?.X ?? 0,
            QueryBoundsY = queryBounds?.Y ?? 0,
            QueryBoundsWidth = queryBounds?.Width ?? 0,
            QueryBoundsHeight = queryBounds?.Height ?? 0,
            ScaleHypothesisCount = scaleHypothesisCount,
            OversizedHypothesisCount = oversizedHypothesisCount,
            UsedRestrictedSearch = usedRestrictedSearch,
            VisibleMaskMilliseconds = visibleMaskMilliseconds,
            VisibleFraction = visibleFraction,
            VisibleStructurePixels = visibleStructurePixels,
            VisibleEdgePixels = visibleEdgePixels,
            VisibleAwareSearchMilliseconds = visibleAwareSearchMilliseconds,
            VisibleAwareCandidateCount = visibleAwareCandidateCount,
            VisibleAwareTopCost = visibleAwareTopCost,
            VisibleAwareTopMargin = visibleAwareTopMargin,
            VisibleAwareEarlyAccepted = visibleAwareEarlyAccepted,
            VisibleAwareFallbackReason = visibleAwareFallbackReason,
            DistanceMapMilliseconds = distanceMapMilliseconds,
            QueryConstructionMilliseconds = queryConstructionMilliseconds,
            HistoryCandidateMilliseconds = historyCandidateMilliseconds,
            FeatureVotingMilliseconds = featureVotingMilliseconds,
            PyramidSearchMilliseconds = pyramidSearchMilliseconds,
            LocalTemplateSearchMilliseconds = localTemplateSearchMilliseconds,
            GlobalTemplateSearchMilliseconds = globalTemplateSearchMilliseconds,
            CandidateRankingMilliseconds = candidateRankingMilliseconds
        };
}

public sealed class MapStructureFeatures : IDisposable
{
    public MapStructureFeatures(
        Mat nuisanceMask,
        Mat structureMask,
        Mat edges,
        Mat? referenceDistanceMap = null,
        Mat? clippedReferenceDistanceMap = null,
        double? clippedDistancePixels = null,
        Mat? normalizedGray = null,
        IReadOnlyList<Mat>? edgePyramid = null,
        KeyPoint[]? keyPoints = null,
        Mat? descriptors = null,
        Mat? repeatedRegionMask = null,
        PreprocessTiming? diagnosticTiming = null,
        Mat? rawVisibleMask = null)
    {
        NuisanceMask = nuisanceMask;
        StructureMask = structureMask;
        Edges = edges;
        ReferenceDistanceMap = referenceDistanceMap;
        ClippedReferenceDistanceMap = clippedReferenceDistanceMap;
        ClippedDistancePixels = clippedDistancePixels;
        NormalizedGray = normalizedGray ?? new Mat();
        EdgePyramid = edgePyramid ?? [];
        KeyPoints = keyPoints ?? [];
        Descriptors = descriptors ?? new Mat();
        RepeatedRegionMask = repeatedRegionMask ?? Mat.Zeros(
            edges.Size(),
            MatType.CV_8UC1).ToMat();
        DiagnosticTiming = diagnosticTiming;
        RawVisibleMask = rawVisibleMask;
    }

    public Mat NuisanceMask { get; }
    public Mat StructureMask { get; }
    public Mat Edges { get; }
    public Mat? ReferenceDistanceMap { get; private set; }
    public Mat? ClippedReferenceDistanceMap { get; private set; }
    public double? ClippedDistancePixels { get; private set; }
    public Mat NormalizedGray { get; }
    public IReadOnlyList<Mat> EdgePyramid { get; }
    public KeyPoint[] KeyPoints { get; }
    public Mat Descriptors { get; }
    public Mat RepeatedRegionMask { get; }
    public PreprocessTiming? DiagnosticTiming { get; }
    public Mat? RawVisibleMask { get; }

    /// <summary>按需创建匹配用的腐蚀掩码。调用者负责释放。</summary>
    public Mat? CreateSafeVisibleMask(int erodePixels = 1)
    {
        if (RawVisibleMask is null || RawVisibleMask.Empty())
            return null;
        var safe = new Mat();
        var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(1 + erodePixels * 2, 1 + erodePixels * 2));
        Cv2.Erode(RawVisibleMask, safe, kernel);
        return safe;
    }

    public Mat GetOrCreateReferenceDistanceMap()
    {
        if (ReferenceDistanceMap is { } existing && !existing.Empty())
            return existing;
        using var inverse = new Mat();
        Cv2.BitwiseNot(Edges, inverse);
        var distance = new Mat();
        Cv2.DistanceTransform(
            inverse,
            distance,
            DistanceTypes.L2,
            DistanceTransformMasks.Precise);
        ReferenceDistanceMap = distance;
        return distance;
    }

    public Mat GetOrCreateClippedReferenceDistanceMap(double clipPixels)
    {
        if (ClippedReferenceDistanceMap is { } existing
            && !existing.Empty()
            && ClippedDistancePixels is { } existingClip
            && Math.Abs(existingClip - clipPixels) < 0.0001d)
        {
            return existing;
        }
        ClippedReferenceDistanceMap?.Dispose();
        var distance = GetOrCreateReferenceDistanceMap().Clone();
        Cv2.Min(distance, clipPixels, distance);
        ClippedReferenceDistanceMap = distance;
        ClippedDistancePixels = clipPixels;
        return distance;
    }

    public MapStructureFeatures Clone() => new(
        NuisanceMask.Clone(),
        StructureMask.Clone(),
        Edges.Clone(),
        ReferenceDistanceMap?.Clone(),
        ClippedReferenceDistanceMap?.Clone(),
        ClippedDistancePixels,
        NormalizedGray.Clone(),
        EdgePyramid.Select(level => level.Clone()).ToArray(),
        KeyPoints.ToArray(),
        Descriptors.Clone(),
        RepeatedRegionMask.Clone(),
        rawVisibleMask: RawVisibleMask?.Clone());

    public void Dispose()
    {
        NuisanceMask.Dispose();
        StructureMask.Dispose();
        Edges.Dispose();
        ReferenceDistanceMap?.Dispose();
        ClippedReferenceDistanceMap?.Dispose();
        NormalizedGray.Dispose();
        foreach (var level in EdgePyramid)
            level.Dispose();
        Descriptors.Dispose();
        RepeatedRegionMask.Dispose();
        RawVisibleMask?.Dispose();
    }
}
