using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class AdaptiveScaleCoordinatorTests
{
    [Fact]
    public void Vpsg3ValidatedEvidenceDirectLocksScaleWithoutUniqueMatches()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions());
        using var frame = Frame();
        var recognition = Recognition(1.2312);
        var vpsg = new AdaptiveVpsgEvidence(
            Validated: true,
            Scale: 1.2312,
            Confidence: 0.85,
            UniqueMatches: 0,
            PairVotes: 0,
            ResidualPixels: 0.0,
            RelativeMad: 0.0,
            Mode: "Vpsg3");
        var decision = coordinator.EvaluateInitial(
            recognition,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.04, true, vpsg),
            openId: 1);

        Assert.Equal(AdaptiveScaleReliability.Reliable, decision.Reliability);
        Assert.Equal(
            AdaptiveScaleReliabilityReason.VpsgDirectLock,
            decision.ReliabilityReason);
        Assert.True(decision.AllowReliableSession);
        Assert.Equal(1, decision.ConsecutiveHighQualityCount);
        Assert.Equal(vpsg.Scale, decision.RecognitionToRender.Result.OverlayTransform!.ScaleX, 6);
    }

    [Fact]
    public void Vpsg3RealisticConfidenceDirectLocksScale()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions());
        using var frame = Frame();
        var recognition = Recognition(0.81177);
        var vpsg = new AdaptiveVpsgEvidence(
            Validated: true,
            Scale: 0.81177,
            Confidence: 0.5513,
            UniqueMatches: 0,
            PairVotes: 0,
            ResidualPixels: 0.0,
            RelativeMad: 0.0,
            Mode: "Vpsg3");
        var decision = coordinator.EvaluateInitial(
            recognition,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.04, true, vpsg),
            openId: 1);

        Assert.Equal(AdaptiveScaleReliability.Reliable, decision.Reliability);
        Assert.Equal(
            AdaptiveScaleReliabilityReason.VpsgDirectLock,
            decision.ReliabilityReason);
        Assert.True(decision.AllowReliableSession);
        Assert.Equal(vpsg.Scale, decision.RecognitionToRender.Result.OverlayTransform!.ScaleX, 5);
    }

    [Fact]
    public void Vpsg2WithModerateConfidenceDoesNotDirectLock()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions());
        using var frame = Frame();
        var recognition = Recognition(0.81177);
        var vpsg = new AdaptiveVpsgEvidence(
            Validated: true,
            Scale: 0.81177,
            Confidence: 0.5513,
            UniqueMatches: 0,
            PairVotes: 0,
            ResidualPixels: 0.0,
            RelativeMad: 0.0,
            Mode: "Vpsg2");
        var decision = coordinator.EvaluateInitial(
            recognition,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.04, true, vpsg),
            openId: 1);

        Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
        Assert.Equal(
            AdaptiveScaleReliabilityReason.None,
            decision.ReliabilityReason);
        Assert.False(decision.AllowReliableSession);
    }
}
