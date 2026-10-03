using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Tests;

public sealed partial class AdaptiveScaleInitialStreakTests
{
    [Fact]
    public void ReliableScaleSurvivesRepeatedFixedGeometryWithoutAdditionalVotes()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        for (var open = 1; open <= 5; open++)
            Vote(coordinator, frame, map, open, 1.57476884614911, floor: "b1f");
        for (var open = 6; open <= 40; open++)
        {
            var decision = coordinator.EvaluateInitial(
                Recognition(map, "b1f", 1.57476884614911), frame, MapFeatureCacheSource.Recovery,
                new AdaptiveScaleInitialEvidence(open, .04d, StructureValidated: true,
                    ScaleIndependentlyEstimated: false), open);
            Assert.Equal(5, decision.ConsecutiveHighQualityCount);
            Assert.Equal(AdaptiveScaleReliability.Reliable, decision.Reliability);
            Assert.True(decision.AllowReliableSession);
            Assert.Equal(1.57476884614911, decision.RecognitionToRender.Result.OverlayTransform!.ScaleX);
        }
    }

}
