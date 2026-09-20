using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleRegressionTests
{
    [Fact]
    public void ProvisionalAdaptiveRebuildPreservesSideEntranceRouteIdentity()
    {
        var map = Map();
        var recognition = Recognition(map, 1.06, 0.72);
        var sideSession = new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = "1f",
            LockedTransform = Transform(1.02),
            BaselineGateScale = 1.02,
            LastConfidence = 0.65,
            SideEntranceScanPriorConfidence = 0.65,
            HasGatePairLock = false
        };

        var rebuilt = MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
            recognition.Map,
            recognition.Result,
            pendingSideEntranceSeed: null,
            previous: sideSession,
            canReusePrevious: false);

        Assert.NotSame(sideSession, rebuilt);
        Assert.Equal(1.06, rebuilt.LockedTransform.ScaleX, 8);
        Assert.Equal(0.65, rebuilt.SideEntranceScanPriorConfidence, 8);
        Assert.False(rebuilt.HasGatePairLock);
        Assert.Equal(
            SelectedAlignmentRoute.SideEntrance,
            MapOpenAlignmentRouteRules.ResolveMatchRoute(
                FirstScanStrategy.SideEntrance,
                rebuilt));
    }

    [Fact]
    public void SideEntranceIdentityCannotLeakAcrossFloorsDuringSessionRebuild()
    {
        var map = Map();
        var primary = new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = "1f",
            LockedTransform = Transform(0.736d),
            BaselineGateScale = 0.736d,
            LastConfidence = 0.92d,
            SideEntranceScanPriorConfidence = 0.93d,
            HasGatePairLock = false
        };

        var basement = MapAlignmentSession
            .RebuildPreservingFirstScanIdentity(
                primary,
                map,
                Recognition(map, 0.4671d, 0.58d, "b1f").Result);

        Assert.Equal("b1f", basement.FloorKey);
        Assert.Equal(0.4671d, basement.LockedTransform.ScaleX, 8);
        Assert.Equal(0d, basement.SideEntranceScanPriorConfidence);
        Assert.Empty(basement.LockedGateEvidence);
    }

    [Fact]
    public void DoubleGateStrategyRemainsOnDefaultRoute()
    {
        Assert.Equal(
            SelectedAlignmentRoute.Default,
            MapOpenAlignmentRouteRules.ResolveMatchRoute(
                FirstScanStrategy.DoubleGate,
                session: null));
    }

    [Fact]
    public void EstablishedSessionRouteOverridesMidMatchSettingChange()
    {
        var sideSession = new MapAlignmentSession
        {
            SideEntranceScanPriorConfidence = 0.65,
            HasGatePairLock = false
        };
        var doubleGateSession = new MapAlignmentSession
        {
            SideEntranceScanPriorConfidence = 0d,
            HasGatePairLock = true
        };

        Assert.Equal(
            SelectedAlignmentRoute.SideEntrance,
            MapOpenAlignmentRouteRules.ResolveMatchRoute(
                FirstScanStrategy.DoubleGate,
                sideSession));
        Assert.Equal(
            SelectedAlignmentRoute.Default,
            MapOpenAlignmentRouteRules.ResolveMatchRoute(
                FirstScanStrategy.SideEntrance,
                doubleGateSession));
    }

    [Fact]
    public async Task ChallengedInitialRecognitionKeepsReliableRuntimeScale()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        var key = Key(map, frame);
        await StabilizeAsync(coordinator, frame, map, key, openId: 1, scale: 1.0);

        var decision = coordinator.EvaluateInitial(
            Recognition(map, 1.02),
            frame,
            null,
            Evidence(4),
            openId: 1);

        Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
        Assert.Equal(1.0, decision.RecognitionToRender.Result.OverlayTransform!.ScaleX, 8);
    }

    [Fact]
    public void StaleContextCannotMutateActiveController()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        var key = Key(map, frame);
        coordinator.EvaluateInitial(
            Recognition(map, 1.0), frame, null, Evidence(1), openId: 7);

        var stale = coordinator.EvaluateOrbObservation(
            key with { FloorKey = "2f" },
            7,
            Transform(1.02),
            1.02,
            DateTimeOffset.UtcNow);
        var current = coordinator.EvaluateOrbObservation(
            key,
            7,
            Transform(1.02),
            1.02,
            DateTimeOffset.UtcNow);

        Assert.False(stale.RequestStructureProbe);
        Assert.Equal(1.02, stale.Transform.ScaleX, 8);
        Assert.Equal(1.0, current.Transform.ScaleX, 8);
    }

    [Fact]
    public async Task InitialVoteIsPersistedBeforeOpenEnds()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-regression-");
        try
        {
            var store = new AdaptiveScaleStore(
                Path.Combine(directory.FullName, "adaptive-scale-cache.json"));
            var coordinator = Coordinator(store);
            using var frame = Frame();
            var map = Map();
            var key = Key(map, frame);
            coordinator.EvaluateInitial(
                Recognition(map, 1.0), frame, null, Evidence(1), 11);
            await coordinator.DrainAsync();

            Assert.Equal(1, store.TryGet(key)!.DistinctOpenCount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OrbConsensusDoesNotAdvanceInitialStreak()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-regression-");
        try
        {
            var store = new AdaptiveScaleStore(
                Path.Combine(directory.FullName, "adaptive-scale-cache.json"));
            var coordinator = Coordinator(store);
            using var frame = Frame();
            var map = Map();
            var key = Key(map, frame);
            await StabilizeAsync(coordinator, frame, map, key, 12, 1.0);
            await coordinator.DrainAsync();

            Assert.Equal(1, store.TryGet(key)!.DistinctOpenCount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConsistentRecoveryFramesReplaceTemporaryScaleWithoutBecomingReliable()
    {
        var coordinator = RecoveryCoordinator();
        using var frame = Frame();
        var map = Map();
        var key = Key(map, frame);
        coordinator.EvaluateInitial(
            Recognition(map, 1.08584, 0.70),
            frame,
            MapFeatureCacheSource.CrossResolutionValidated,
            Evidence(1),
            openId: 31);

        var first = await ObserveAsync(
            coordinator,
            key,
            31,
            Recognition(map, 1.0960, 0.70),
            2);
        var second = await ObserveAsync(
            coordinator,
            key,
            31,
            Recognition(map, 1.0964, 0.71),
            3);

        Assert.Null(first.PendingConsensus);
        Assert.NotNull(second.PendingConsensus);
        Assert.True(second.PendingConsensus!.IsProvisionalRecovery);
        var consensusScale = second.PendingConsensus.Scale;
        var committed = coordinator.CommitStructureConsensus(
            key,
            31,
            Recognition(map, consensusScale, 0.71),
            second.PendingConsensus,
            0.04);

        Assert.True(committed.ScaleChanged);
        Assert.False(committed.BecameReliable);
        Assert.Equal(AdaptiveScaleState.Provisional, committed.State);
        var held = coordinator.EvaluateOrbObservation(
            key,
            31,
            Transform(1.12),
            1.02,
            DateTimeOffset.UtcNow);
        Assert.Equal(consensusScale, held.Transform.ScaleX, 8);
    }

    [Fact]
    public async Task MediumConfidenceRecoveryCannotReplaceReliableLockedScale()
    {
        var coordinator = RecoveryCoordinator();
        using var frame = Frame();
        var map = Map();
        var key = Key(map, frame);
        await StabilizeAsync(coordinator, frame, map, key, 32, 1.0);
        coordinator.ObserveStructureFailure(key, 32);
        coordinator.ObserveStructureFailure(key, 32);

        var first = await ObserveAsync(
            coordinator,
            key,
            32,
            Recognition(map, 1.04, 0.70),
            10);
        var second = await ObserveAsync(
            coordinator,
            key,
            32,
            Recognition(map, 1.0402, 0.71),
            11);
        var orb = coordinator.EvaluateOrbObservation(
            key,
            32,
            Transform(1.04),
            1.04,
            DateTimeOffset.UtcNow);

        Assert.Null(first.PendingConsensus);
        Assert.Null(second.PendingConsensus);
        Assert.Equal(1.0, orb.Transform.ScaleX, 8);
        Assert.Equal(AdaptiveScaleState.Recovering, orb.State);
    }

    [Fact]
    public async Task FloorSwitchSuspendsActiveKeyButRestoresLockedFloorWithinOpen()
    {
        var coordinator = Coordinator();
        using var frame = Frame();
        var map = Map();
        var firstKey = Key(map, frame);
        await StabilizeAsync(coordinator, frame, map, firstKey, 41, 1.0);

        coordinator.SuspendActiveFloor(41, "switch-to-2f");
        Assert.True(coordinator.TryGetPreferredSeed(firstKey, 41, out var saved));
        Assert.Equal(AdaptiveScaleSeedSource.Runtime, saved!.Source);
        var stale = coordinator.EvaluateOrbObservation(
            firstKey,
            41,
            Transform(1.04),
            1.04,
            DateTimeOffset.UtcNow);
        Assert.Equal(1.04, stale.Transform.ScaleX, 8);

        var second = coordinator.EvaluateInitial(
            Recognition(map, 1.2, floor: "2f"),
            frame,
            null,
            Evidence(20),
            41);
        Assert.Equal(AdaptiveScaleReliability.Provisional, second.Reliability);
        coordinator.SuspendActiveFloor(41, "switch-to-1f");
        var resumed = coordinator.EvaluateInitial(
            Recognition(map, 1.0),
            frame,
            null,
            Evidence(21),
            41);

        Assert.Equal(1.0, resumed.RecognitionToRender.Result.OverlayTransform!.ScaleX, 8);
        Assert.Equal(AdaptiveScaleReliability.Reliable, resumed.Reliability);
        coordinator.EndOpen(41, "map-closed");
        Assert.False(coordinator.TryGetPreferredSeed(firstKey, 41, out _));
    }

    private static async Task StabilizeAsync(
        AdaptiveScaleCoordinator coordinator,
        CapturedGameFrame frame,
        MapRecord map,
        AdaptiveScaleKey key,
        long openId,
        double scale)
    {
        var recognition = Recognition(map, scale);
        coordinator.EvaluateInitial(
            recognition, frame, null, Evidence(1), openId);
        await ObserveAsync(coordinator, key, openId, recognition, 2);
        var observed = await ObserveAsync(coordinator, key, openId, recognition, 3);
        Assert.NotNull(observed.PendingConsensus);
        var committed = coordinator.CommitStructureConsensus(
            key, openId, recognition, observed.PendingConsensus!, 0.04);
        Assert.True(committed.BecameReliable);
    }

    private static async Task<AdaptiveStructureDecision> ObserveAsync(
        AdaptiveScaleCoordinator coordinator,
        AdaptiveScaleKey key,
        long openId,
        RuntimeMapRecognition recognition,
        long frameId)
    {
        await Task.Delay(60);
        return coordinator.EvaluateStructureObservation(
            key, openId, recognition, frameId, 0.04);
    }

    private static AdaptiveScaleCoordinator Coordinator(AdaptiveScaleStore? store = null) =>
        new(
            new AdaptiveScaleOptions { MinimumObservationSpacingMilliseconds = 50 },
            store);

    private static AdaptiveScaleCoordinator RecoveryCoordinator() =>
        new(
            new AdaptiveScaleOptions
            {
                ReliableConfidence = 0.75d,
                RecoveryConfidence = 0.65d,
                MinimumObservationSpacingMilliseconds = 50
            });

    private static AdaptiveScaleInitialEvidence Evidence(long frameId) =>
        new(frameId, 0.04, StructureValidated: true);

    private static AdaptiveScaleKey Key(MapRecord map, CapturedGameFrame frame) =>
        AdaptiveScaleKey.Create(map, "1f", frame.ClientBounds, frame.ViewportBounds);

    private static CapturedGameFrame Frame() => new(
        new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
        new MapScreenRect(0, 0, 1920, 1080),
        new MapScreenRect(303, 25, 1314, 1055),
        IntPtr.Zero);

    private static MapRecord Map() => new()
    {
        Id = Guid.NewGuid(),
        UpdatedAt = DateTimeOffset.UnixEpoch,
        Title = "test"
    };

    private static RuntimeMapRecognition Recognition(
        MapRecord map,
        double scale,
        double confidence = 0.90,
        string floor = "1f") => new()
    {
        Map = map,
        Result = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = floor,
            Confidence = confidence,
            LocalizationConfidence = confidence,
            IdentityConfidence = 0.90,
            OverlayTransform = Transform(scale),
            StructureCandidateMargin = 0.10,
            StructureRejectionReason = MapStructureRejectionReason.None,
            EvidenceKind = MapAlignmentEvidenceKind.Structure
        }
    };

    private static MapOverlayTransform Transform(double scale) => new()
    {
        ScaleX = scale,
        ScaleY = scale,
        ReferenceCenterX = 500,
        ReferenceCenterY = 400,
        ScreenCenterX = 600,
        ScreenCenterY = 500,
        ReferenceWidth = 1000,
        ReferenceHeight = 800,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };
}
