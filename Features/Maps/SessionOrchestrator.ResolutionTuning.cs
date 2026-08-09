using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Builds the per-run structure tuning from the persisted user baseline
    /// plus the active resolution preset. Keeping this as a copy avoids
    /// leaking one resolution's overrides into the next hot-swap.
    /// </summary>
    private MapStructureRegistrationTuning CreateEffectiveStructureTuning()
    {
        var tuning = _settings!.StructureRegistrationTuning.Clone();
        var structure = _config.Get<IDVBuff.Core.Models.StructureConfig>("structure");
        tuning.MaximumChamferPixels = structure.MaximumChamferPixels;
        tuning.MinimumEdgeCoverage = structure.MinimumEdgeCoverage;
        tuning.MinimumOccupancyCoverage = structure.MinimumOccupancyCoverage;
        tuning.MinimumCandidateMargin = structure.MinimumCandidateMargin;
        tuning.EdgeDistanceTolerancePixels = structure.EdgeDistanceTolerancePixels;
        tuning.DistanceClipPixels = structure.DistanceClipPixels;

        var scale = _config.Get<IDVBuff.Core.Models.ScaleConfig>("scale");
        tuning.ScaleSearchRadius = scale.SearchRadius;
        tuning.ScaleSearchStep = scale.SearchStep;
        tuning.TrackingScaleSearchRadius = scale.TrackingScaleSearchRadius;

        var coarse = _config.Get<IDVBuff.Core.Models.CoarseConfig>("coarse");
        tuning.EnableFastAlignment = coarse.EnableFastAlignment;
        tuning.FastFallbackToLegacy = coarse.FastFallbackToLegacy;
        tuning.FastCoarseMaxDimension = coarse.FastCoarseMaxDimension;
        tuning.FastCoarseDownsampleFactor = coarse.FastCoarseDownsampleFactor;
        tuning.FastCoarseTopK = coarse.FastCoarseTopK;
        tuning.FastCoarseNmsRadius = coarse.FastCoarseNmsRadius;

        var ecc = _config.Get<IDVBuff.Core.Models.EccConfig>("ecc");
        tuning.EnableEccRefinement = ecc.EnableEccRefinement;
        tuning.SkipEccScoreThreshold = ecc.SkipEccScoreThreshold;

        var feature = _config.Get<FeatureVotingConfig>("feature_voting");
        tuning.EnableFeatureVoting = feature.Enable;
        tuning.FeatureRatioThreshold = feature.RatioThreshold;
        tuning.FeatureInlierTolerancePixels = feature.InlierTolerancePixels;

        var early = _config.Get<EarlyTerminationConfig>("early_termination");
        tuning.EarlyTerminationScoreThreshold = early.ScoreThreshold;

        var visible = _config.Get<VisibleAwareConfig>("visible_aware");
        tuning.EnableVisibleMask = visible.EnableMask;
        tuning.EnableVisibleAwareShadow = visible.EnableShadow;
        tuning.EnableVisibleAwareInjection = visible.EnableInjection;
        tuning.EnableVisibleAwareEarlyExit = visible.EnableEarlyExit;
        tuning.VisibleAwareSearchBudgetMilliseconds = visible.SearchBudgetMs;
        tuning.VisibleAwareCoarseDownsample = visible.CoarseDownsample;
        tuning.VisibleAwareTopK = visible.TopK;
        tuning.VisibleAwareMinimumVisibleFraction = visible.MinVisibleFraction;
        tuning.VisibleAwareMinimumVisibleStructurePixels =
            visible.MinVisibleStructurePixels;
        tuning.SafeVisibleMaskErodePixels = visible.SafeErodePixels;
        tuning.VisibleVMin = visible.VMin;
        tuning.VisibleSMin = visible.SMin;
        tuning.VisibleHighlightVMin = visible.HighlightVMin;
        tuning.VisibleAwareEarlyTerminationMaxCompositeCost =
            visible.EarlyTerminationMaxCompositeCost;

        tuning.Normalize();
        return tuning;
    }

    private MapStructureRegistrationTuning CreateInitialAlignmentStructureTuning()
    {
        var tuning = CreateEffectiveStructureTuning();
        tuning.StructureFallbackBudgetMilliseconds = Math.Max(
            3000,
            tuning.StructureFallbackBudgetMilliseconds);
        tuning.ScaleSearchStep = Math.Min(0.005d, tuning.ScaleSearchStep);
        tuning.Normalize();
        return tuning;
    }

    private MapRecognitionTuning CreateInitialAlignmentRecognitionTuning()
    {
        var tuning = _settings!.RecognitionTuning.Clone();
        tuning.WarmGateSearchBudgetMs = Math.Max(
            500,
            tuning.WarmGateSearchBudgetMs);
        tuning.Normalize();
        return tuning;
    }
}
