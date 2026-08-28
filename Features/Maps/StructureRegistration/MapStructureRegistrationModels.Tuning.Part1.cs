using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Maps;
public sealed partial class MapStructureRegistrationTuning
{

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
        if (SchemaVersion < 8)
            Generation ??= new();
        SchemaVersion = CurrentSchemaVersion;
        Generation ??= new();
        Generation.Normalize();
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
        MinimumEdgeCoverage = Finite(MinimumEdgeCoverage, 0.40d, 0.1d, 0.98d);
        MinimumOccupancyCoverage = Finite(MinimumOccupancyCoverage, 0.42d, 0.1d, 0.98d);
        MinimumCandidateMargin = Finite(MinimumCandidateMargin, 0.04d, 0.01d, 0.8d);
        LocalSearchRadiusRatio = Finite(LocalSearchRadiusRatio, 0.20d, 0.02d, 1d);
        // Recovery policy deliberately uses 0.15 initially and 0.30 as the
        // final uncalibrated fallback.  Clamping here to 0.05 silently made
        // both runtime stages execute the same narrow search.
        ScaleSearchRadius = Finite(ScaleSearchRadius, 0.02d, 0d, 0.70d);
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
        FastCoarseTopK = Math.Clamp(
            FastCoarseTopK,
            Channel == MapAlignmentChannel.LowStructure ? 2 : 3,
            20);
        FastCoarseNmsRadius = Math.Clamp(FastCoarseNmsRadius, 4, 40);
        FastCoarseMaxDimension = Math.Clamp(FastCoarseMaxDimension, 40, 400);
        FastCoarseMinimumTemplateDimension = Math.Clamp(
            FastCoarseMinimumTemplateDimension, 4, 200);
        MinimumUsableScale = Finite(
            MinimumUsableScale, 0.05d, 0.001d, 8d);
        LowStructureMinimumScale = Finite(
            LowStructureMinimumScale, 0.40d, 0.05d, 8d);
        LowStructureMaximumScale = Finite(
            LowStructureMaximumScale, 1.60d, 0.05d, 8d);
        if (LowStructureMaximumScale < LowStructureMinimumScale)
            (LowStructureMinimumScale, LowStructureMaximumScale) =
                (LowStructureMaximumScale, LowStructureMinimumScale);
        LowStructureScaleHypothesisCount = Math.Clamp(
            LowStructureScaleHypothesisCount, 3, 31);
        EdgeCoverageWeight = Finite(EdgeCoverageWeight, 4d, 0d, 20d);
        ChamferWeight = Finite(ChamferWeight, 1d, 0d, 20d);
        OccupancyCoverageWeight = Finite(OccupancyCoverageWeight, 2d, 0d, 20d);
        ReferenceCoverageWeight = Finite(ReferenceCoverageWeight, 4d, 0d, 20d);
        PartitionPenaltyWeight = Finite(PartitionPenaltyWeight, 0.75d, 0d, 20d);
        PriorDisagreementWeight = Finite(PriorDisagreementWeight, 0.75d, 0d, 20d);
        MinimumEdgesPerPartition = Math.Clamp(
            MinimumEdgesPerPartition, 1, 10000);
        MinimumPartitionCoverage = Finite(
            MinimumPartitionCoverage, 0.45d, 0.01d, 1d);
        BoundsPenalty = Finite(BoundsPenalty, 100d, 0d, 10000d);
        MaximumScaleChangeRatio = Finite(
            MaximumScaleChangeRatio, 0.15d, 0d, 8d);
        MinimumPriorAgreement = Finite(
            MinimumPriorAgreement, 0.05d, 0d, 1d);
        GlobalSearchMarginMultiplier = Finite(
            GlobalSearchMarginMultiplier, 1.25d, 1d, 10d);
        MarginNormalizationFloor = Finite(
            MarginNormalizationFloor, 0.01d, 0.000001d, 1d);
        ScaleDuplicateTolerance = Finite(
            ScaleDuplicateTolerance, 0.000001d, 0.000000001d, 1d);
        SpatialDuplicateTolerance = Finite(
            SpatialDuplicateTolerance, 2d, 0d, 1000d);
        CandidateDuplicateRadius = Finite(
            CandidateDuplicateRadius, 1d, 0d, 1000d);
        RefinementWorsenTolerance = Finite(
            RefinementWorsenTolerance, 0.001d, 0d, 100d);
        LowStructureWarmPathBudgetMilliseconds = Math.Clamp(
            LowStructureWarmPathBudgetMilliseconds, 50, 300);
        LowStructureColdPathBudgetMilliseconds = Math.Clamp(
            LowStructureColdPathBudgetMilliseconds, 50, 700);
        LowStructureEndToEndBudgetMilliseconds = Math.Clamp(
            LowStructureEndToEndBudgetMilliseconds, 100, 1200);
        LowStructureMaximumScalesPerFrame = Math.Clamp(
            LowStructureMaximumScalesPerFrame, 1, 3);
        LowStructureTranslationTopK = Math.Clamp(
            LowStructureTranslationTopK, 1, 2);
        LowStructureReadinessFrameCount = Math.Clamp(
            LowStructureReadinessFrameCount, 2, 5);
        LowStructureScaleConsistencyTolerance = Finite(
            LowStructureScaleConsistencyTolerance, 0.015d, 0.001d, 0.05d);
        LowStructureCacheConfirmationCount = Math.Clamp(
            LowStructureCacheConfirmationCount, 2, 8);
        LowStructureMinimumReferenceCoverage = Finite(
            LowStructureMinimumReferenceCoverage, 0.50d, 0.1d, 0.98d);
        LowStructureMinimumProjectionCorrelation = Finite(
            LowStructureMinimumProjectionCorrelation, 0.75d, 0.1d, 0.99d);
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
