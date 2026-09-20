using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOpenSteadyScaleRecoveryTests
{
    [Fact]
    public void RecoveryRunsOneHonestWideScaleSearch()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            ScaleSearchRadius = 0.02d,
            TrackingScaleSearchRadius = 0.01d,
            DisableScaleEarlyTermination = false,
            EnableFastAlignment = true,
            EnableFeatureVoting = true,
            EnforceTimeBudget = true
        };

        MapOpenAlignmentRouteRules.ApplySteadyScaleRecoveryPolicy(tuning);

        Assert.Equal(MapOpenAlignmentRouteRules.SteadyScaleRecoverySearchRadius, tuning.ScaleSearchRadius);
        Assert.Equal(0d, tuning.TrackingScaleSearchRadius);
        Assert.False(tuning.DisableScaleEarlyTermination);
        Assert.True(tuning.EnableFastAlignment);
        Assert.False(tuning.EnableFeatureVoting);
        Assert.True(tuning.EnforceTimeBudget);
    }

    [Theory]
    [InlineData(MapAlignmentChannel.Standard, MapStructureRejectionReason.WeakAbsoluteScore, true)]
    [InlineData(MapAlignmentChannel.Standard, MapStructureRejectionReason.ScaleChangeTooLarge, true)]
    [InlineData(MapAlignmentChannel.Standard, MapStructureRejectionReason.NativeScaleChanged, true)]
    [InlineData(MapAlignmentChannel.Standard, MapStructureRejectionReason.AmbiguousCandidates, false)]
    [InlineData(MapAlignmentChannel.LowStructure, MapStructureRejectionReason.WeakAbsoluteScore, false)]
    public void RecoveryIsRestrictedToStandardScaleEvidence(
        MapAlignmentChannel channel,
        MapStructureRejectionReason rejectionReason,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ShouldAttemptSteadyScaleRecovery(
                channel,
                rejectionReason));
    }

    [Theory]
    [InlineData(1.1410782264692587, 1.180, true)]
    [InlineData(1.1410782264692587, 1.142, false)]
    public void RecoveryOnlyResetsStateForMaterialChange(
        double previousScale,
        double recoveredScale,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.HasMaterialScaleChange(
                previousScale,
                recoveredScale));
    }
}
