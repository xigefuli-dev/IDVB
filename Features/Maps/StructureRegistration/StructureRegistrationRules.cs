using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

/// <summary>Internal structure-registration safety rules; overridable via IConfigProvider.</summary>
internal static class StructureRegistrationRules
{
    private static StructureConfig _structure = new();
    private static ScaleConfig _scale = new();
    private static EccConfig _ecc = new();
    private static CoarseConfig _coarse = new();

    // ═══════════════════════════════════════════════════════════════
    // 基本容差 (alignment.structure)
    // ═══════════════════════════════════════════════════════════════
    public static double ScaleDiffTolerance => _structure.ScaleDiffTolerance;
    public static double RotationTolerance => _structure.RotationTolerance;
    public static double MinimumUsableScale => _structure.MinimumUsableScale;
    public static double ScaleAgreementTolerance => _structure.ScaleAgreementTolerance;
    public static double ScaleDuplicateTolerance => _structure.ScaleDuplicateTolerance;
    public static double SpatialDuplicateTolerance => _structure.SpatialDuplicateTolerance;
    public static double CandidateDuplicateRadius => _structure.CandidateDuplicateRadius;

    // Scale search is kept in the resolution preset because the same map
    // viewport can have a materially different effective scale at different
    // client resolutions.
    public static double ScaleSearchRadius => _scale.SearchRadius;
    public static double TrackingScaleSearchRadius =>
        _scale.TrackingScaleSearchRadius;

    // ═══════════════════════════════════════════════════════════════
    // 历史候选 (alignment.structure)
    // ═══════════════════════════════════════════════════════════════
    public static int MaxHistoryCandidates => _structure.MaxHistoryCandidates;

    // ═══════════════════════════════════════════════════════════════
    // 评估公式权重 (alignment.structure)
    // ═══════════════════════════════════════════════════════════════
    public static int MinEdgesPerPartition => _structure.MinEdgesPerPartition;
    public static double MinPartitionCoverage => _structure.MinPartitionCoverage;
    public static double EdgeCoverageWeight => _structure.EdgeCoverageWeight;
    public static double OccupancyCoverageWeight => _structure.OccupancyCoverageWeight;
    public static double BoundsPenalty => _structure.BoundsPenalty;
    public static double PartitionPenaltyWeight => _structure.PartitionPenaltyWeight;
    public static double PriorDisagreementPenaltyWeight => _structure.PriorDisagreementPenaltyWeight;

    // ═══════════════════════════════════════════════════════════════
    // ORB 特征匹配 (alignment.structure)
    // ═══════════════════════════════════════════════════════════════
    public static int FeatureMinVotes => _structure.FeatureMinVotes;
    public static double FeatureMinInlierTolerance => _structure.FeatureMinInlierTolerance;
    public static double FeatureConsensusCostReduction => _structure.FeatureConsensusCostReduction;
    public static double FeatureWeightEpsilon => _structure.FeatureWeightEpsilon;
    public static int FeatureVoteCountDivisor => _structure.FeatureVoteCountDivisor;

    // ═══════════════════════════════════════════════════════════════
    // 验证阈值 (alignment.structure)
    // ═══════════════════════════════════════════════════════════════
    public static double StrictChamferFactor => _structure.StrictChamferFactor;
    public static double StrictEdgeCoverageMargin => _structure.StrictEdgeCoverageMargin;
    public static double StrictOccupancyMargin => _structure.StrictOccupancyMargin;
    public static double MinimumPriorAgreement => _structure.MinimumPriorAgreement;
    public static double StrictPriorAgreement => _structure.StrictPriorAgreement;
    public static double MinimumReplacementMargin => _structure.MinimumReplacementMargin;
    public static double MinimumTrustedFeatureConsensus => _structure.MinimumTrustedFeatureConsensus;
    public static int IsStrongCandidateMinPartitions => _structure.IsStrongCandidateMinPartitions;
    public static int CanSkipRefinementMinPartitions => _structure.CanSkipRefinementMinPartitions;
    public static int EarlyTermMinPartitions => _structure.EarlyTermMinPartitions;
    public static int EarlyTermExtraPartitions => _structure.EarlyTermExtraPartitions;
    public static double EarlyTermMarginFactor => _structure.EarlyTermMarginFactor;
    public static double GlobalSearchMarginMultiplier => _structure.GlobalSearchMarginMultiplier;
    public static double MarginNormalizationFloor => _structure.MarginNormalizationFloor;

    // ═══════════════════════════════════════════════════════════════
    // 局部精修 (alignment.structure)
    // ═══════════════════════════════════════════════════════════════
    public static int[] RefinementSteps => _structure.RefinementSteps;
    public static double RefinementEarlyExitScore => _structure.RefinementEarlyExitScore;
    public static double RefinementWorsenTolerance => _structure.RefinementWorsenTolerance;
    public static double RefinementChamferFactor => _structure.RefinementChamferFactor;
    public static double RefinementEdgeCoverageMargin => _structure.RefinementEdgeCoverageMargin;
    public static double RefinementOccupancyMargin => _structure.RefinementOccupancyMargin;

    // ═══════════════════════════════════════════════════════════════
    // ECC 精修 (alignment.ecc)
    // ═══════════════════════════════════════════════════════════════
    public static int EccMaxIterations => _ecc.MaxIterations;
    public static double EccEpsilon => _ecc.Epsilon;
    public static int EccGaussFocalLen => _ecc.GaussFocalLen;
    public static double EccMinCorrelation => _ecc.MinCorrelation;
    public static double EccMaxTranslationShift => _ecc.MaxTranslationShift;

    // ═══════════════════════════════════════════════════════════════
    // 粗搜索 (alignment.coarse)
    // ═══════════════════════════════════════════════════════════════
    public static int CoarseDownsampleFactor => _coarse.DownsampleFactor;
    public static int CoarseMinRefDimension => _coarse.MinRefDimension;
    public static int CoarseMinTplDimension => _coarse.MinTplDimension;
    public static int CoarseFastCoarseMinTemplateDim => _coarse.FastCoarseMinTemplateDim;
    public static int PyramidMinLevels => _coarse.PyramidMinLevels;
    public static int PyramidDownsampleFactor => _coarse.PyramidDownsampleFactor;
    public static int CoarseSuppressionDivisor => _coarse.SuppressionDivisor;
    public static int CollectCandidatesSuppressionDivisor => _coarse.CollectCandidatesSuppressionDivisor;

    /// <summary>Apply a pre-populated StructureConfig instance.</summary>
    internal static void ApplyConfig(StructureConfig structure)
    {
        _structure = structure ?? new StructureConfig();
    }

    /// <summary>Apply a pre-populated scale-search configuration.</summary>
    internal static void ApplyConfig(ScaleConfig scale)
    {
        _scale = scale ?? new ScaleConfig();
    }

    /// <summary>Apply a pre-populated EccConfig instance.</summary>
    internal static void ApplyConfig(EccConfig ecc)
    {
        _ecc = ecc ?? new EccConfig();
    }

    /// <summary>Apply a pre-populated CoarseConfig instance.</summary>
    internal static void ApplyConfig(CoarseConfig coarse)
    {
        _coarse = coarse ?? new CoarseConfig();
    }

    /// <summary>Read and apply configuration from an IConfigProvider.</summary>
    internal static void ApplyConfig(IConfigProvider provider)
    {
        _structure = provider.Get<StructureConfig>("structure") ?? new StructureConfig();
        _scale = provider.Get<ScaleConfig>("scale") ?? new ScaleConfig();
        _ecc = provider.Get<EccConfig>("ecc") ?? new EccConfig();
        _coarse = provider.Get<CoarseConfig>("coarse") ?? new CoarseConfig();
    }

    /// <summary>
    /// Adjacent scale hypotheses that resolve to the same reference location
    /// are one alignment peak, not competing map locations.
    /// </summary>
    public static bool IsSameAlignmentBasin(
        MapStructureCandidate first,
        MapStructureCandidate second,
        MapStructureRegistrationTuning tuning)
    {
        var scaleTolerance = Math.Max(
            0.001d,
            Math.Max(first.Scale, second.Scale)
                * Math.Max(0.005d, tuning.ScaleSearchRadius));
        if (Math.Abs(first.Scale - second.Scale) > scaleTolerance)
            return false;

        var minimumDistance = Math.Max(10d, tuning.MinimumSpanPixels / 2d);
        var offsetDistance = Math.Sqrt(
            Math.Pow(first.OffsetX - second.OffsetX, 2d)
            + Math.Pow(first.OffsetY - second.OffsetY, 2d));
        var referenceDistance = Math.Sqrt(
            Math.Pow(first.ReferenceX - second.ReferenceX, 2d)
            + Math.Pow(first.ReferenceY - second.ReferenceY, 2d));
        return offsetDistance < minimumDistance
            || referenceDistance < minimumDistance;
    }
}
