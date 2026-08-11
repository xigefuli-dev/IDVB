using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOpenAlignmentRouteTests
{
    [Theory]
    [InlineData(false, false, 0.85d, true)]
    [InlineData(false, false, 0d, false)]
    [InlineData(false, true, 0.85d, false)]
    [InlineData(true, false, 0.85d, false)]
    public void DirectSideFeaturePrecedesScaleCacheOnlyForLockedPrimaryFloor(
        bool isOtherFloor,
        bool recoveringIdentity,
        double sidePrior,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ShouldPreferLockedSideFeature(
                isOtherFloor,
                recoveringIdentity,
                sidePrior));
    }

    [Theory]
    [InlineData(100, 250)]
    [InlineData(1500, 1500)]
    [InlineData(3000, 1800)]
    public void NoDoorRouteUsesOneBoundedAlignmentWorkBudget(
        int configured,
        int expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules
                .ResolveNoDoorAlignmentBudgetMilliseconds(configured));
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public void DeadlinePrioritizesStructureOnlyForSideEntranceRoute(
        int routeValue,
        bool hasAlignmentDeadline,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ShouldPrioritizeStructureValidation(
                (SelectedAlignmentRoute)routeValue,
                hasAlignmentDeadline));
    }

    [Fact]
    public void PerformanceAcceptanceTargetsRemainExplicit()
    {
        Assert.Equal(1000, MapOpenAlignmentRouteRules.TargetP50Milliseconds);
        Assert.Equal(
            1000,
            MapOpenAlignmentRouteRules.MinimumFeatureRecoveryBudgetMilliseconds);
        Assert.Equal(1500, MapOpenAlignmentRouteRules.TargetP95Milliseconds);
        Assert.Equal(
            1800,
            MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds);
        Assert.Equal(0.95d, MapOpenAlignmentRouteRules.TargetReliableAlignmentRate);
        Assert.Equal(
            3d,
            MapOpenAlignmentRouteRules.TargetTranslationJitterP95Pixels);
    }

    [Fact]
    public void VpsgStageBudgetIsExplicit()
    {
        Assert.Equal(600, MapOpenAlignmentRouteRules.VpsgStageBudgetMilliseconds);
        Assert.Equal(
            450,
            MapOpenAlignmentRouteRules.MinimumVpsgStageBudgetMilliseconds);
    }

    [Theory]
    [InlineData(false, 0.30d)]
    [InlineData(true, 0.15d)]
    public void NoDoorRouteHasOneGlobalRecoveryRadius(
        bool calibrated,
        double expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ResolveSingleGlobalRecoveryRadius(
                calibrated));
    }

    [Fact]
    public void ReliableFloorSessionMustMatchExactFloorAndMapVersion()
    {
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var session = new MapAlignmentSession
        {
            MapId = mapId,
            MapUpdatedAt = updatedAt,
            FloorKey = "2f",
            LastConfidence = 0.86d,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = 1.2d,
                ScaleY = 1.2d,
                ReferenceCenterX = 500d,
                ReferenceCenterY = 400d,
                ScreenCenterX = 700d,
                ScreenCenterY = 540d,
                ReferenceWidth = 1000,
                ReferenceHeight = 800,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };

        Assert.True(MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
            session,
            mapId,
            updatedAt,
            "2f",
            0.70d));
        Assert.False(MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
            session,
            mapId,
            updatedAt,
            "1f",
            0.70d));
        Assert.False(MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
            session,
            mapId,
            updatedAt.AddSeconds(1),
            "2f",
            0.70d));
    }
}
