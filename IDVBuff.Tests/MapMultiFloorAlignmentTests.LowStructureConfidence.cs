using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapMultiFloorAlignmentTests
{
    [Fact]
    public void LowStructureConfidenceUsesGeometryInsteadOfGuideMapFill()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var observedPerfectAlignment = new MapStructureCandidate
        {
            ChamferPixels = 1.7314476327160733d,
            ReverseChamferPixels = 4.198827109693317d,
            EdgeCoverage = 0.7568496071145764d,
            OccupancyCoverage = 0.44619265923537826d,
            ReferenceCoverage = 0.9260039538839744d,
            ProjectionCorrelation = 0.6690011189143066d,
            ConsistentPartitions = 4,
            IsWithinValidBounds = true,
            PriorAgreement = 1d
        };

        var observed = MapStructureConfidenceCalculator.Calculate(
            observedPerfectAlignment,
            candidateMargin: 1d,
            tuning);
        var differentGuideMapFill = MapStructureConfidenceCalculator.Calculate(
            observedPerfectAlignment with { OccupancyCoverage = 0.90d },
            candidateMargin: 1d,
            tuning);

        Assert.Equal(0.8471411472090294d, observed.LockConfidence, 12);
        Assert.Equal(
            observed.LockConfidence,
            differentGuideMapFill.LockConfidence,
            12);
        Assert.Equal(0.9260039538839744d, observed.ReferenceCoverage, 12);
        Assert.Equal(4.198827109693317d, observed.ReverseChamferPixels, 12);
    }

    [Fact]
    public void LowStructureConfidenceFallsWithWeakerBidirectionalGeometry()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var strong = new MapStructureCandidate
        {
            ChamferPixels = 1d,
            EdgeCoverage = 0.85d,
            OccupancyCoverage = 0.50d,
            ReferenceCoverage = 0.90d,
            ConsistentPartitions = 4,
            IsWithinValidBounds = true,
            PriorAgreement = 1d
        };
        var weak = strong with
        {
            ChamferPixels = 2.8d,
            EdgeCoverage = 0.52d,
            ReferenceCoverage = 0.51d,
            ConsistentPartitions = 1
        };

        var strongConfidence = MapStructureConfidenceCalculator.Calculate(
            strong,
            candidateMargin: 1d,
            tuning);
        var weakConfidence = MapStructureConfidenceCalculator.Calculate(
            weak,
            candidateMargin: 0.08d,
            tuning);

        Assert.True(strongConfidence.LockConfidence >= 0.85d);
        Assert.True(weakConfidence.LockConfidence < 0.50d);
    }
}
