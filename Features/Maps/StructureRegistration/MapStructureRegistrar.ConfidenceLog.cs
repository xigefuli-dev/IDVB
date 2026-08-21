namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    private static Dictionary<string, object?> CreateConfidenceLogDetails(
        MapStructureConfidenceBreakdown breakdown) => new()
        {
            ["confidence"] = breakdown.LockConfidence,
            ["chamferQuality"] = breakdown.ChamferQuality,
            ["edgeCoverage"] = breakdown.EdgeCoverage,
            ["occupancyCoverage"] = breakdown.OccupancyCoverage,
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
