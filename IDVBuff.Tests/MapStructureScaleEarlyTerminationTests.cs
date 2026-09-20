using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapStructureScaleEarlyTerminationTests
{
    [Fact]
    public void ScaleSearchStopsOnlyForFullyValidatedExtremelyHighConfidence()
    {
        var tuning = new MapStructureRegistrationTuning();
        tuning.Normalize();
        var request = new MapStructureRegistrationRequest
        {
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = 1d,
                ScaleY = 1d,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            },
            ScaleSearchPolicy = MapScaleSearchPolicy.Fixed
        };
        var perfect = Candidate(priorAgreement: 1d);
        var merelyStrong = Candidate(priorAgreement: 0.21d);

        Assert.True(MapStructureScaleSearch.HasExtremelyHighConfidenceCandidate(
            [perfect], tuning, request, out var confidence));
        Assert.Equal(1d, confidence, 8);
        Assert.False(MapStructureScaleSearch.HasExtremelyHighConfidenceCandidate(
            [merelyStrong], tuning, request, out confidence));
        Assert.True(confidence <
            MapStructureScaleSearch.ExtremelyHighConfidenceThreshold);
    }

    private static MapStructureCandidate Candidate(double priorAgreement) => new()
    {
        Scale = 1d,
        ChamferPixels = 0d,
        EdgeCoverage = 1d,
        OccupancyCoverage = 1d,
        ConsistentPartitions = 4,
        CompositeCost = 0.01d,
        FeatureInlierCount = 20,
        FeatureConsensus = 1d,
        PriorAgreement = priorAgreement,
        IsWithinValidBounds = true,
        EccConverged = true,
        EccCorrelation = 1d
    };
}
