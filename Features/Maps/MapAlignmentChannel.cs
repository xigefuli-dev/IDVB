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
            // Low-structure work is bounded by its fixed hypothesis set. Do
            // not turn a performance target into a partial-result deadline.
            EnforceTimeBudget = false,
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
            EnableFeatureVoting = config.EnableFeatureScaleEstimate,
            EnableFastAlignment = false,
            FastFallbackToLegacy = false,
            FastCoarseDownsampleFactor = config.FastCoarseDownsampleFactor,
            FastCoarseTopK = config.FastCoarseTopK,
            FastCoarseNmsRadius = config.FastCoarseNmsRadius,
            FastCoarseMaxDimension = config.FastCoarseMaxDimension,
            FastCoarseMinimumTemplateDimension =
                config.FastCoarseMinimumTemplateDimension,
            MinimumUsableScale = config.MinimumUsableScale,
            Generation = new MapStructureGenerationTuning
            {
                StructureOpenKernelSize = config.StructureOpenKernelSize,
                MinimumEdgeComponentAreaPixels =
                    config.MinimumEdgeComponentAreaPixels,
                EdgeClosingIterations = config.EdgeClosingIterations
            }
        };
        tuning.Normalize();
        return tuning;
    }
}
