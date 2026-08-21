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
    [InlineData(0d, false, 0)]
    [InlineData(0.85d, true, 0)]
    [InlineData(0.85d, false, 1)]
    public void PendingIdentityUsesSideRouteOnlyWithGenuineSideSeed(
        double sidePrior,
        bool hasGatePairLock,
        int expected)
    {
        var session = new MapAlignmentSession
        {
            SideEntranceScanPriorConfidence = sidePrior,
            HasGatePairLock = hasGatePairLock
        };

        Assert.Equal(
            expected,
            (int)MapOpenAlignmentRouteRules.ResolvePendingIdentityRoute(session));
    }

    [Theory]
    [InlineData(100, 250)]
    [InlineData(1500, 1000)]
    [InlineData(3000, 1000)]
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
        Assert.Equal(200, MapOpenAlignmentRouteRules.SteadyAlignmentMaximumMilliseconds);
        Assert.Equal(
            1000,
            MapOpenAlignmentRouteRules.MinimumFeatureRecoveryBudgetMilliseconds);
        Assert.Equal(1000, MapOpenAlignmentRouteRules.InitialAlignmentMaximumMilliseconds);
        Assert.Equal(
            1000,
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

    [Fact]
    public void SteadyGlobalRecoveryExpandsTranslationWithoutChangingScale()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            ScaleSearchRadius = 0.20d,
            TrackingScaleSearchRadius = 0.10d,
            EnableFeatureVoting = true,
            EnforceTimeBudget = true,
            EnableFastAlignment = false,
            FastFallbackToLegacy = false,
            FastCoarseTopK = 2,
            VisibleAwareTopK = 3
        };

        MapOpenAlignmentRouteRules
            .ApplySteadyGlobalTranslationRecoveryPolicy(tuning);

        Assert.Equal(0d, tuning.ScaleSearchRadius);
        Assert.Equal(0d, tuning.TrackingScaleSearchRadius);
        Assert.False(tuning.EnableFeatureVoting);
        Assert.False(tuning.EnforceTimeBudget);
        Assert.True(tuning.EnableFastAlignment);
        Assert.True(tuning.FastFallbackToLegacy);
        Assert.True(tuning.FastCoarseTopK >= 5);
        Assert.True(tuning.VisibleAwareTopK >= 5);
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

    [Fact]
    public void PendingVariantWithoutTransformGetsExactFloorNeutralSeed()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        map.Recognition.FirstFloor.RecognitionPixelWidth = 1000;
        map.Recognition.FirstFloor.RecognitionPixelHeight = 800;
        map.Recognition.SecondFloor.RecognitionPixelWidth = 500;
        map.Recognition.SecondFloor.RecognitionPixelHeight = 400;
        var pending = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "1f",
            IdentityConfidence = 1d,
            LocalizationConfidence = 0d,
            OverlayTransform = null
        };

        var session = MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
            map,
            pending,
            pendingSideEntranceSeed: null,
            previous: null,
            canReusePrevious: false,
            independentFloorKey: "2f");

        Assert.Equal(map.Id, session.MapId);
        Assert.Equal("2f", session.FloorKey);
        Assert.Equal(1d, session.LockedTransform.ScaleX);
        Assert.Equal(500, session.LockedTransform.ReferenceWidth);
        Assert.Equal(400, session.LockedTransform.ReferenceHeight);
        Assert.False(session.HasGatePairLock);
        Assert.Equal(0d, session.SideEntranceScanPriorConfidence);
    }

    [Fact]
    public void OrdinaryTransformlessRecognitionRemainsRejected()
    {
        var map = new MapRecord { Id = Guid.NewGuid() };
        map.Recognition.EnsureStandardAnchors();

        Assert.Throws<InvalidOperationException>(() =>
            MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
                map,
                new MapRecognitionResult { MapId = map.Id, Floor = "1f" },
                pendingSideEntranceSeed: null,
                previous: null,
                canReusePrevious: false));
    }

    [Fact]
    public void AlignmentContextNormalizesFloorButKeepsCaptureGeometryIndependent()
    {
        var context = new MapAlignmentContextKey(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            " 2F ",
            2560,
            1600,
            1322,
            1053,
            " edges-v3 ").Normalize();

        Assert.Equal("2f", context.FloorKey);
        Assert.Equal("edges-v3", context.StructureGeneration);
        Assert.NotEqual(
            context,
            context with { ViewportWidth = context.ViewportWidth + 1 });
    }
}
