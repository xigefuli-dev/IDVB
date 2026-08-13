// IDVB Remaster Phase 0.4 — Core Model
// TOML 配置文件对应的 POCO 类型。这些类型用于 Tommy/Tomlyn 反序列化。

namespace IDVBuff.Core.Models;

/// <summary>全局默认配置 POCO。</summary>
public sealed class DefaultConfig
{
    public SessionConfig Session { get; init; } = new();
    public ConfidenceConfig Confidence { get; init; } = new();
    public StabilityConfig Stability { get; init; } = new();
    public EvidenceWeightsConfig EvidenceWeights { get; init; } = new();
    public TrackingConfig Tracking { get; init; } = new();
    public AuxiliaryConfig Auxiliary { get; init; } = new();
    public SideEntranceConfig SideEntrance { get; init; } = new();
    public PipelineConfig Pipeline { get; init; } = new();
}

// --- Sub-configs ---

public sealed class SessionConfig
{
    public int OpeningAnimationDelayMs { get; init; } = 10;
    public int OpeningTimeoutMs { get; init; } = 3000;
    public int StableFrameIntervalMs { get; init; } = 20;
    public int StableFrameCount { get; init; } = 2;
    public double StableFrameDifference { get; init; } = 0.015;
    public int PresencePollingMs { get; init; } = 200;
    public int PlayerPollingMs { get; init; } = 100;
    public int WindowValidationMs { get; init; } = 500;
    public int IntegrityCheckIntervalMs { get; init; } = 2000;
}

public sealed class ConfidenceConfig
{
    public double High { get; init; } = 0.82;
    public double Medium { get; init; } = 0.62;
    public int MediumConfidenceFrames { get; init; } = 3;
    public int BackgroundFailureFrames { get; init; } = 3;
    public double MinimumPlayerConfidence { get; init; } = 0.70;
    public double ConfirmationAdvantage { get; init; } = 0.08;
}

public sealed class StabilityConfig
{
    public double PositionTolerancePixels { get; init; } = 3;
    public double ScaleToleranceRatio { get; init; } = 0.003;
    public double RotationToleranceDegrees { get; init; } = 0.1;
    public int MaxHistoryEntries { get; init; } = 5;
}

public sealed class EvidenceWeightsConfig
{
    public double AnchorGeometry { get; init; } = 0.20;
    public double FeatureConsensus { get; init; } = 0.15;
    public double CandidateSeparation { get; init; } = 0.10;
    public double StructureQuality { get; init; } = 0.25;
    public double RefinementQuality { get; init; } = 0.10;
    public double BoundsAndPrior { get; init; } = 0.10;
    public double TemporalStability { get; init; } = 0.10;
}

public sealed class TrackingConfig
{
    public int PlayerStalenessMs { get; init; } = 500;
    public int MaxCalibrationEntries { get; init; } = 128;
    public int PreviousAlignmentSearchRadiusPx { get; init; } = 96;
    public int TrackingSearchRadiusPx { get; init; } = 48;
}

public sealed class AuxiliaryConfig
{
    public int MaximumTemplates { get; init; } = 4;
    public double DirectLockConfidence { get; init; } = 0.82;
    public double MinimumAnchorScore { get; init; } = 0.78;
    public double MinimumDistanceRatio { get; init; } = 0.05;
    public double BaseTolerance { get; init; } = 6.0;
    public double ViewportToleranceRatio { get; init; } = 0.005;
}

public sealed class SideEntranceConfig
{
    public int FeatureRadius { get; init; } = 80;
    public bool UseAuxiliaryAnchorRecognition { get; init; }
    public bool ReusePreviousAlignmentResult { get; init; } = true;
}

public sealed class PipelineConfig
{
    public bool UseNewPipeline { get; init; }
    public int FloorRecognitionBudgetMs { get; init; } = 350;
    public int PresenceMissingFrameThreshold { get; init; } = 2;
    public int MonitorPollingIntervalMs { get; init; } = 50;
    public int NonMonitoredDelayMs { get; init; } = 100;
}

/// <summary>分辨率专属配置 POCO。</summary>
public sealed class ResolutionPreset
{
    public string Name { get; init; } = string.Empty;
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int Dpi { get; init; } = 120;
    public DetectionConfig Detection { get; init; } = new();
    public RecognitionConfig Recognition { get; init; } = new();
    public AlignmentConfig Alignment { get; init; } = new();
}

// --- Resolution-specific sub-configs ---

public sealed class DetectionConfig
{
    public GateConfig Gate { get; init; } = new();
    public FloorConfig Floor { get; init; } = new();
    public PlayerMarkerConfig PlayerMarker { get; init; } = new();
}

public sealed class GateConfig
{
    public double TemplateScaleFactor { get; init; } = 1.0;
    public double MatchThreshold { get; init; } = 0.72;
    public int NmsWindowSize { get; init; } = 30;
    public int RoiMarginTop { get; init; } = 80;
    public int RoiMarginBottom { get; init; } = 60;
}

public sealed class FloorConfig
{
    public double TemplateScaleFactor { get; init; } = 1.0;
    public double MatchThreshold { get; init; } = 0.85;
}

public sealed class PlayerMarkerConfig
{
    public int MinRadius { get; init; } = 4;
    public int MaxRadius { get; init; } = 14;
    public int[] ColorHueRange { get; init; } = [200, 260];
    public double MatchThreshold { get; init; } = 0.70;
}

public sealed class RecognitionConfig
{
    public GeometryConfig Geometry { get; init; } = new();
    public CandidateConfig Candidate { get; init; } = new();
}

public sealed class GeometryConfig
{
    public double VectorErrorTolerance { get; init; } = 0.04;
    public double AmbiguityMargin { get; init; } = 0.015;
    public double ConfirmationMargin { get; init; } = 0.08;
    public double MinimumConfidence { get; init; } = 0.50;
    public double VectorScoreWeight { get; init; } = 0.65;
    public double DistanceScoreWeight { get; init; } = 0.25;
    public double AngleScoreWeight { get; init; } = 0.10;
    public double GeometryScoreWeight { get; init; } = 0.85;
    public double TemplateScoreWeight { get; init; } = 0.15;
}

public sealed class CandidateConfig
{
    public bool ForceCandidateSelection { get; init; } = true;
    public bool ForceBestResult { get; init; }
    public int TopCandidates { get; init; } = 4;
    public int WarmGateSearchBudgetMs { get; init; } = 120;
    public int ConfirmationGateSearchBudgetMs { get; init; }
    public double ConfirmationRoiTemplatePaddingFactor { get; init; } = 1.0;
    public int ConfirmationRoiMinimumPaddingPixels { get; init; } = 24;
    public int ConfirmationMaximumMapDragPixelsPerSecond { get; init; } = 600;
    public int ConfirmationSchedulingSlackMs { get; init; } = 100;
}

// --- Alignment config ---

public sealed class AlignmentConfig
{
    public StructureConfig Structure { get; init; } = new();
    public CoarseConfig Coarse { get; init; } = new();
    public ScaleConfig Scale { get; init; } = new();
    public EccConfig Ecc { get; init; } = new();
    public RefineConfig Refine { get; init; } = new();
    public FeatureVotingConfig FeatureVoting { get; init; } = new();
    public PartitionsConfig Partitions { get; init; } = new();
    public EarlyTerminationConfig EarlyTermination { get; init; } = new();
    public VisibleAwareConfig VisibleAware { get; init; } = new();
    public CompositeCostConfig CompositeCost { get; init; } = new();
}

public sealed class StructureConfig
{
    public double MaximumChamferPixels { get; init; } = 3.2;
    /// <summary>受限搜索（RestrictSearchToLockedTransform=true）专用的 chamfer 上限。</summary>
    public double RestrictedSearchMaximumChamferPixels { get; init; } = 3.0;
    public double MinimumEdgeCoverage { get; init; } = 0.40;
    public double MinimumOccupancyCoverage { get; init; } = 0.42;
    public double MinimumCandidateMargin { get; init; } = 0.04;
    public double EdgeDistanceTolerancePixels { get; init; } = 2.25;
    public double DistanceClipPixels { get; init; } = 12;
}

public sealed class CoarseConfig
{
    public int FastCoarseMaxDimension { get; init; } = 200;
    public int FastCoarseDownsampleFactor { get; init; } = 2;
    public int FastCoarseTopK { get; init; } = 5;
    public int FastCoarseNmsRadius { get; init; } = 12;
    public bool EnableFastAlignment { get; init; } = true;
    public bool FastFallbackToLegacy { get; init; } = true;
}

public sealed class ScaleConfig
{
    public double SearchRadius { get; init; } = 0.02;
    public double SearchStep { get; init; } = 0.01;
    public double TrackingScaleSearchRadius { get; init; } = 0.005;
    public double MinimumScale { get; init; } = 0.05;
    public double MaximumScale { get; init; } = 8.0;
    public double ScaleChangeRejectionRatio { get; init; } = 0.03;
}

public sealed class EccConfig
{
    public bool EnableEccRefinement { get; init; }
    public int SkipEccScoreThreshold { get; init; } = 8;
    public int MaxIterations { get; init; } = 30;
    public double Epsilon { get; init; } = 0.0001;
    public double MinCorrelation { get; init; } = 0.60;
    public double MaxTranslationShift { get; init; } = 2.5;
}

public sealed class RefineConfig
{
    public int[] Steps { get; init; } = [8, 4, 2, 1];
    public double SkipRefinementThreshold { get; init; } = 0.001;
    public double SkipRefinementMargin { get; init; } = 0.10;
}

public sealed class FeatureVotingConfig
{
    public bool Enable { get; init; } = true;
    public double RatioThreshold { get; init; } = 0.64;
    public double InlierTolerancePixels { get; init; } = 6;
    public int MinInlierTolerance { get; init; } = 2;
    public int MinVotes { get; init; } = 3;
    public double ConsensusCostReduction { get; init; } = 0.5;
}

/// <summary>
/// Experimental frame-to-frame ORB tracking applied after an absolute map
/// alignment has been locked. It is intentionally not exposed in settings.
/// </summary>
public sealed class OrbTrackingConfig
{
    public bool Enabled { get; init; }
    public int ActiveIntervalMs { get; init; } = 100;
    public int StableIntervalMs { get; init; } = 250;
    public int StableObservationCount { get; init; } = 5;
    public int FeatureCount { get; init; } = 1200;
    public double RatioThreshold { get; init; } = 0.64;
    public int MinimumMatches { get; init; } = 12;
    public int MinimumRansacInliers { get; init; } = 8;
    public double MinimumInlierRatio { get; init; } = 0.50;
    public double MaximumMedianReprojectionErrorPixels { get; init; } = 2.5;
    public double MaximumRotationDegrees { get; init; } = 0.5;
    public double MaximumStepScaleChangeRatio { get; init; } = 0.01;
    public double MaximumBaselineScaleChangeRatio { get; init; } = 0.03;
    public double MinimumTranslationLimitPixels { get; init; } = 24;
    public double MaximumTranslationPixelsPerSecond { get; init; } = 600;
    public double TranslationDeadbandPixels { get; init; } = 0.5;
    public double ScaleDeadbandRatio { get; init; } = 0.0005;
    public int StructureCorrectionIntervalMs { get; init; } = 1000;
    public int WeakFrameThreshold { get; init; } = 3;
    public int RecoveryIntervalMs { get; init; } = 500;
}

public sealed class PartitionsConfig
{
    public int MinEdgesPerPartition { get; init; } = 12;
    public double MinCoverage { get; init; } = 0.45;
}

public sealed class EarlyTerminationConfig
{
    public double ScoreThreshold { get; init; } = 0.55;
    public double PriorAgreementMin { get; init; } = 0.20;
    public double ChamferFactor { get; init; } = 0.85;
    public double EdgeOccupancyBonus { get; init; } = 0.10;
    public double MarginFactor { get; init; } = 1.5;
}

public sealed class VisibleAwareConfig
{
    public bool EnableMask { get; init; }
    public bool EnableShadow { get; init; }
    public bool EnableInjection { get; init; } = true;
    public bool EnableEarlyExit { get; init; } = true;
    public int SearchBudgetMs { get; init; } = 150;
    public int CoarseDownsample { get; init; } = 4;
    public int TopK { get; init; } = 5;
    public double MinVisibleFraction { get; init; } = 0.05;
    public int MinVisibleStructurePixels { get; init; } = 50;
    public int SafeErodePixels { get; init; } = 1;
    public int VMin { get; init; } = 42;
    public int SMin { get; init; } = 14;
    public int HighlightVMin { get; init; } = 80;
    public double EarlyTerminationMaxCompositeCost { get; init; } = 0.55;
}

public sealed class CompositeCostConfig
{
    public double ChamferWeight { get; init; } = 1.0;
    public double EdgeCoverageWeight { get; init; } = 4.0;
    public double OccupancyWeight { get; init; } = 2.0;
    public double PartitionWeight { get; init; } = 0.75;
    public double BoundsPenalty { get; init; } = 100.0;
    public double PriorDisagreementWeight { get; init; } = 0.75;
}
