using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapOpenAlignmentRouteTests
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
    [InlineData(false, false, "exact-floor-vpsg")]
    [InlineData(true, false, "validated-fixed-scale")]
    [InlineData(false, true, "side-entrance")]
    [InlineData(true, true, "validated-fixed-scale")]
    public void SelectedMapInitialAlignmentPrefersRealEvidence(
        bool hasValidatedStructureScaleSeed,
        bool hasSideEntranceSeed,
        string expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ResolveInitialAlignmentRoute(
                hasValidatedStructureScaleSeed,
                hasSideEntranceSeed));
    }

    [Fact]
    public void ScanAcceptedFormalMustShortCircuitFurtherScaleFallback()
    {
        var map = new MapRecord { Id = Guid.NewGuid() };
        var accepted = new MapRecognitionAttempt
        {
            StructureAccepted = true,
            Recognition = new RuntimeMapRecognition
            {
                Map = map,
                Result = new MapRecognitionResult
                {
                    MapId = map.Id,
                    IdentityConfidence = 0.70d,
                    LocalizationConfidence = 0.10d,
                    StructureCandidateMargin = 0d
                }
            }
        };

        Assert.True(
            MapOpenAlignmentRouteRules
                .ShouldShortCircuitScanVerification(accepted));
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
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void MapCacheSaveRequiresOpenBigMapAndLockedIdentity(
        bool isBigMapOpen,
        bool isMapIdentityLocked,
        bool isSurvey,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.CanSaveMapCache(
                isBigMapOpen,
                isMapIdentityLocked,
                isSurvey));
    }

    [Theory]
    [InlineData(MapAlignmentChannel.Standard, false, false)]
    [InlineData(MapAlignmentChannel.Standard, true, true)]
    [InlineData(MapAlignmentChannel.LowStructure, false, false)]
    [InlineData(MapAlignmentChannel.LowStructure, true, true)]
    public void EveryWarmStateRequiresReliableSameFloorScale(
        MapAlignmentChannel channel,
        bool isScaleReliable,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.CanUseWarmAlignmentState(
                channel,
                isScaleReliable));
    }

    [Theory]
    [InlineData(true, false, 0d, true)]
    [InlineData(false, false, 0.51d, false)]
    [InlineData(false, false, 0.52d, true)]
    [InlineData(true, true, 0.90d, false)]
    public void InitialSideSeedAlwaysReceivesItsPromisedGlobalRecovery(
        bool isInitialSeed,
        bool accepted,
        double localConfidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ShouldAttemptSideEntranceGlobalRecovery(
                isInitialSeed,
                accepted,
                localConfidence));
    }

    [Theory]
    [InlineData(false, true, 0d, false, true)]
    [InlineData(false, true, 0.85d, false, false)]
    [InlineData(false, false, 0d, false, false)]
    [InlineData(false, true, 0d, true, false)]
    [InlineData(true, false, 0d, false, true)]
    public void IndependentFloorAlignmentCoversOtherFloorsAndUnalignedNeutralSessions(
        bool isOtherFloor,
        bool isPendingVariantAlignment,
        double sidePrior,
        bool hasGatePairLock,
        bool expected)
    {
        var session = new MapAlignmentSession
        {
            SideEntranceScanPriorConfidence = sidePrior,
            HasGatePairLock = hasGatePairLock
        };

        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ShouldUseIndependentFloorAlignment(
                isOtherFloor,
                isPendingVariantAlignment,
                session));
    }

    [Fact]
    public void ProvisionalPrimaryStructureCannotSeedTheNextFixedScaleAttempt()
    {
        var provisionalWrongSession = new MapAlignmentSession
        {
            FloorKey = "1f",
            Mode = MapAlignmentTrackingMode.StructureMatched,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = 1.70d,
                ScaleY = 1.70d,
                OffsetX = 639d,
                OffsetY = 332.9d
            },
            LastConfidence = 0.7281d,
            SideEntranceScanPriorConfidence = 0d,
            HasGatePairLock = false
        };

        Assert.True(
            MapOpenAlignmentRouteRules.ShouldUseIndependentFloorAlignment(
                isOtherFloor: false,
                isPendingVariantAlignment: false,
                provisionalWrongSession));
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
    [InlineData(1, true, false, true)]
    [InlineData(1, false, false, false)]
    [InlineData(1, false, true, true)]
    [InlineData(0, true, true, false)]
    public void DeadlineOrCurrentScanEvidencePrioritizesSideStructure(
        int routeValue,
        bool hasAlignmentDeadline,
        bool hasCurrentScanGateEvidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapOpenAlignmentRouteRules.ShouldPrioritizeStructureValidation(
                (SelectedAlignmentRoute)routeValue,
                hasAlignmentDeadline,
                hasCurrentScanGateEvidence));
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
    public void ScanVerificationUsesExplicitBudgetAndPercentileTargets()
    {
        Assert.Equal(150, MapOpenAlignmentRouteRules.ScanVerificationBudgetMilliseconds);
        Assert.Equal(
            30,
            MapOpenAlignmentRouteRules.ScanVerificationMinimumCandidateBudgetMilliseconds);
        Assert.Equal(
            50,
            MapOpenAlignmentRouteRules.ScanVerificationMinimumVpsgBudgetMilliseconds);
        Assert.Equal(
            100,
            MapOpenAlignmentRouteRules.ScanVerificationFormalStructureBudgetMilliseconds);
        Assert.Equal(80, MapOpenAlignmentRouteRules.ScanVerificationVpsgBudgetMilliseconds);
        Assert.Equal(100, MapOpenAlignmentRouteRules.ScanVerificationP50Milliseconds);
        Assert.Equal(200, MapOpenAlignmentRouteRules.ScanVerificationP90Milliseconds);
        Assert.Equal(350, MapOpenAlignmentRouteRules.ScanVerificationP99Milliseconds);

        var samples = Enumerable.Range(1, 100).Select(value => (double)value);
        Assert.Equal(50.5d, MapOpenAlignmentRouteRules.Percentile(samples, 0.50d));
        Assert.Equal(90.1d, MapOpenAlignmentRouteRules.Percentile(samples, 0.90d), 10);
        Assert.Equal(99.01d, MapOpenAlignmentRouteRules.Percentile(samples, 0.99d), 10);
    }

    [Theory]
    [InlineData(MapStructureRejectionReason.NoCandidate, true)]
    [InlineData(MapStructureRejectionReason.AmbiguousCandidates, true)]
    [InlineData(MapStructureRejectionReason.WeakAbsoluteScore, true)]
    [InlineData(MapStructureRejectionReason.RefinementFailed, true)]
    [InlineData(MapStructureRejectionReason.InsufficientStructure, false)]
    [InlineData(MapStructureRejectionReason.QueryLargerThanReference, false)]
    public void ScanFastFallbackOnlyAllowsBoundaryFailures(
        MapStructureRejectionReason reason,
        bool expected)
    {
        var result = MapStructureRegistrationResult.Reject(reason);

        Assert.Equal(
            expected,
            MapStructureRegistrar.ShouldRunScanLegacyFallback(result));
    }

    [Fact]
    public void ScanCheapRejectIsDisabledByDefaultAndCloned()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            Mode = MapStructureRegistrationMode.ScanVerification
        };

        Assert.False(tuning.EnableScanCheapReject);
        tuning.EnableScanCheapRejectShadowCollection = true;
        Assert.False(tuning.Clone().EnableScanCheapReject);
        Assert.True(tuning.Clone().EnableScanCheapRejectShadowCollection);
        Assert.Equal(
            450,
            MapOpenAlignmentRouteRules
                .ScanVerificationShadowCollectionBudgetMilliseconds);
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
    [InlineData(false, 0.70d)]
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
            targetFloorKey: "2f");

        Assert.Equal(map.Id, session.MapId);
        Assert.Equal("2f", session.FloorKey);
        Assert.Equal(1d, session.LockedTransform.ScaleX);
        Assert.Equal(500, session.LockedTransform.ReferenceWidth);
        Assert.Equal(400, session.LockedTransform.ReferenceHeight);
        Assert.False(session.HasGatePairLock);
        Assert.Equal(0d, session.SideEntranceScanPriorConfidence);
    }

    [Fact]
    public void SecondaryFloorSessionsCanNeverSeedPrimaryFloor()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        map.Recognition.FirstFloor.RecognitionPixelWidth = 1000;
        map.Recognition.FirstFloor.RecognitionPixelHeight = 800;
        var secondaryTransform = new MapOverlayTransform
        {
            ScaleX = 0.46666d,
            ScaleY = 0.46666d,
            ReferenceWidth = 700,
            ReferenceHeight = 600,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        var secondaryResult = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "b1f",
            OverlayTransform = secondaryTransform
        };
        var secondarySession = new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = "b1f",
            LockedTransform = secondaryTransform,
            BaselineGateScale = secondaryTransform.ScaleX
        };

        var primarySession =
            MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
                map,
                secondaryResult,
                pendingSideEntranceSeed: secondarySession,
                previous: secondarySession,
                canReusePrevious: true,
                targetFloorKey: "1f");

        Assert.Equal("1f", primarySession.FloorKey);
        Assert.Equal(1d, primarySession.LockedTransform.ScaleX);
        Assert.Equal(1000, primarySession.LockedTransform.ReferenceWidth);
        Assert.NotSame(secondarySession, primarySession);
    }

}
