namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    private static Dictionary<string, object?> CreateConfidenceLogDetails(
        MapStructureConfidenceBreakdown breakdown) => new()
        {
            ["confidence"] = breakdown.LockConfidence,
            ["chamferPixels"] = breakdown.ChamferPixels,
            ["chamferQuality"] = breakdown.ChamferQuality,
            ["reverseChamferPixels"] = breakdown.ReverseChamferPixels,
            ["edgeCoverage"] = breakdown.EdgeCoverage,
            ["occupancyCoverage"] = breakdown.OccupancyCoverage,
            ["referenceCoverage"] = breakdown.ReferenceCoverage,
            ["projectionCorrelation"] = breakdown.ProjectionCorrelation,
            ["partitionQuality"] = breakdown.PartitionQuality,
            ["geometricFitQuality"] = breakdown.GeometricFitQuality,
            ["evidenceConfidence"] = breakdown.EvidenceConfidence,
            ["geometricLockConfidence"] = breakdown.GeometricLockConfidence,
            ["lockConfidence"] = breakdown.LockConfidence,
            ["candidateMargin"] = breakdown.CandidateSeparation,
            ["hardGateFailure"] = breakdown.HardGateFailure,
            ["lowEvidenceReason"] = breakdown.LowEvidenceReason
        };
}
