using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public enum MapAlignmentChannel
{
    Standard,
    LowStructure
}

public sealed record MapAlignmentChannelDescriptor(
    MapAlignmentChannel Channel,
    string Name,
    string DiagnosticLabel);

/// <summary>Resolves and configures the channel for one exact target floor.</summary>
public static class MapAlignmentChannelRegistry
{
    public static MapAlignmentChannelDescriptor Standard { get; } =
        new(MapAlignmentChannel.Standard, "Standard", "standard");

    public static MapAlignmentChannelDescriptor LowStructure { get; } =
        new(MapAlignmentChannel.LowStructure, "LowStructure", "low_structure");

    public static MapAlignmentChannelDescriptor Resolve(
        MapRecord map,
        string floorKey)
    {
        var floor = MapFloorRules.GetOrderedFloors(map)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Key,
                floorKey,
                StringComparison.Ordinal));
        return floor is not null
            && MapFloorMarkerRules.Has(floor.MarkerKeys, MapFloorMarkerRules.LowStructure)
            ? LowStructure
            : Standard;
    }

    public static MapStructureRegistrationTuning CreateLowStructure(
        LowStructureConfig? config = null)
    {
        config ??= new LowStructureConfig();
        // Build from a low-structure-owned baseline. Never clone the standard
        // channel: resolution hot reloads must not leak standard values here.
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
            Channel = MapAlignmentChannel.LowStructure,
            EnforceTimeBudget = true,
            StructureFallbackBudgetMilliseconds = Math.Clamp(
                config.ColdPathBudgetMilliseconds, 50, 700),
            MaximumChamferPixels =
                MapStructureRegistrationTuning.LockedMaximumChamferPixels,
            RestrictedSearchMaximumChamferPixels =
                MapStructureRegistrationTuning.LockedMaximumChamferPixels,
            MinimumEdgeCoverage = config.MinimumEdgeCoverage,
            MinimumOccupancyCoverage = config.MinimumOccupancyCoverage,
            MinimumCandidateMargin = config.MinimumCandidateMargin,
            MinimumConsistentPartitions = config.MinimumConsistentPartitions,
            TopCandidateCount = Math.Max(3, config.TopCandidateCount),
            MinimumEdgePixels = config.MinimumEdgePixels,
            MinimumSpanPixels = config.MinimumSpanPixels,
            ScaleSearchStep = config.ScaleSearchStep,
            TrackingScaleSearchRadius = config.TrackingScaleSearchRadius,
            EdgeDistanceTolerancePixels = config.EdgeDistanceTolerancePixels,
            DistanceClipPixels = config.DistanceClipPixels,
            LowStructureMinimumScale = config.MinimumScale,
            LowStructureMaximumScale = config.MaximumScale,
            LowStructureScaleHypothesisCount = config.ScaleHypothesisCount,
            EdgeCoverageWeight = config.EdgeCoverageWeight,
            ChamferWeight = config.ChamferWeight,
            OccupancyCoverageWeight = config.OccupancyCoverageWeight,
            ReferenceCoverageWeight = config.ReferenceCoverageWeight,
            PartitionPenaltyWeight = config.PartitionPenaltyWeight,
            PriorDisagreementWeight = config.PriorDisagreementWeight,
            MinimumEdgesPerPartition = config.MinimumEdgesPerPartition,
            MinimumPartitionCoverage = config.MinimumPartitionCoverage,
            BoundsPenalty = config.BoundsPenalty,
            MaximumScaleChangeRatio = config.MaximumScaleChangeRatio,
            MinimumPriorAgreement = config.MinimumPriorAgreement,
            GlobalSearchMarginMultiplier = config.GlobalSearchMarginMultiplier,
            MarginNormalizationFloor = config.MarginNormalizationFloor,
            ScaleDuplicateTolerance = config.ScaleDuplicateTolerance,
            SpatialDuplicateTolerance = config.SpatialDuplicateTolerance,
            CandidateDuplicateRadius = config.CandidateDuplicateRadius,
            RefinementWorsenTolerance = config.RefinementWorsenTolerance,
            DisableScaleEarlyTermination = true,
            EnableFeatureVoting = false,
            LowStructureEnableFeatureScaleEstimate =
                config.EnableFeatureScaleEstimate,
            // The low-structure hard gates compare both directions. The
            // reverse direction is only meaningful inside the part of the
            // live viewport that is actually visible, so this channel must
            // always prepare that mask. It is consumed by the evaluator; the
            // separate visible-aware search remains disabled to keep the
            // bounded low-cost search path intact.
            EnableVisibleMask = true,
            EnableVisibleAwareShadow = false,
            EnableVisibleAwareInjection = false,
            EnableVisibleAwareEarlyExit = false,
            EnableFastAlignment = false,
            FastFallbackToLegacy = false,
            FastCoarseDownsampleFactor = config.FastCoarseDownsampleFactor,
            FastCoarseTopK = Math.Clamp(
                Math.Min(config.TranslationTopK, config.FastCoarseTopK),
                1,
                2),
            FastCoarseNmsRadius = config.FastCoarseNmsRadius,
            FastCoarseMaxDimension = config.FastCoarseMaxDimension,
            FastCoarseMinimumTemplateDimension =
                config.FastCoarseMinimumTemplateDimension,
            MinimumUsableScale = config.MinimumUsableScale,
            Generation = new MapStructureGenerationTuning
            {
                CannyLowThreshold = config.CannyLowThreshold,
                CannyHighThreshold = config.CannyHighThreshold,
                StructureCloseKernelSize = config.StructureCloseKernelSize,
                StructureOpenKernelSize = config.StructureOpenKernelSize,
                LiveGradientSupportRadiusPixels =
                    config.LiveGradientSupportRadiusPixels,
                MinimumEdgeComponentAreaPixels =
                    config.MinimumEdgeComponentAreaPixels,
                EdgeClosingIterations = config.EdgeClosingIterations
            }
        };
        tuning.LowStructureWarmPathBudgetMilliseconds = Math.Clamp(
            config.WarmPathBudgetMilliseconds, 50, 300);
        tuning.LowStructureColdPathBudgetMilliseconds = Math.Clamp(
            config.ColdPathBudgetMilliseconds, 50, 700);
        tuning.LowStructureEndToEndBudgetMilliseconds = Math.Clamp(
            config.EndToEndBudgetMilliseconds, 100, 1200);
        tuning.LowStructureMaximumScalesPerFrame = Math.Clamp(
            config.MaximumScalesPerFrame, 1, 3);
        tuning.LowStructureTranslationTopK = Math.Clamp(
            Math.Min(config.TranslationTopK, config.FastCoarseTopK),
            1,
            2);
        tuning.LowStructureReadinessFrameCount = Math.Clamp(
            config.ReadinessFrameCount, 2, 5);
        tuning.LowStructureScaleConsistencyTolerance = Math.Clamp(
            config.ScaleConsistencyTolerance, 0.001d, 0.05d);
        tuning.LowStructureCacheConfirmationCount = Math.Clamp(
            config.CacheConfirmationCount, 2, 8);
        tuning.LowStructureMinimumReferenceCoverage = Math.Clamp(
            config.MinimumReferenceCoverage, 0.1d, 0.98d);
        tuning.LowStructureMinimumProjectionCorrelation = Math.Clamp(
            config.MinimumProjectionCorrelation, 0.1d, 0.99d);
        tuning.Normalize();
        return tuning;
    }
}
