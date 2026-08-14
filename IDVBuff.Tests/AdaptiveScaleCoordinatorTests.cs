using IDVBuff.Features.Maps;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleCoordinatorTests
{
    [Fact]
    public void CrossResolutionSeedRemainsProvisionalAfterOneStrongFrame()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions());
        using var frame = Frame();

        var decision = coordinator.EvaluateInitial(
            Recognition(1.08),
            frame,
            MapFeatureCacheSource.CrossResolutionValidated,
            Evidence(),
            openId: 1);

        Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
        Assert.False(decision.AllowLegacyCacheWrite);
        Assert.False(decision.AllowHotStartMemory);
        Assert.True(decision.StartOrbTracking);
    }

    [Fact]
    public void StrongPlayerSeedStillRequiresSidecarConsensus()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions());
        using var frame = Frame();

        var decision = coordinator.EvaluateInitial(
            Recognition(1.10),
            frame,
            MapFeatureCacheSource.Player,
            Evidence(),
            openId: 1);

        Assert.Equal(AdaptiveScaleReliability.Provisional, decision.Reliability);
        Assert.False(decision.AllowLegacyCacheWrite);
        Assert.False(decision.AllowReliableSession);
    }

    [Fact]
    public void OrbObservationCannotDirectlyPublishScale()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions());
        using var frame = Frame();
        var recognition = Recognition(1.0);
        coordinator.EvaluateInitial(
            recognition,
            frame,
            MapFeatureCacheSource.CrossResolutionValidated,
            Evidence(),
            openId: 1);

        var orb = coordinator.EvaluateOrbObservation(
            AdaptiveScaleKey.Create(
                recognition.Map,
                recognition.Result.Floor,
                frame.ClientBounds,
                frame.ViewportBounds),
            1,
            Transform(1.01),
            1.01,
            DateTimeOffset.UtcNow);

        Assert.Equal(1.0, orb.Transform.ScaleX, 8);
        Assert.Equal(1.0, orb.Transform.ScaleY, 8);
        Assert.True(orb.RequestStructureProbe);
    }

    [Fact]
    public void DisabledModuleReturnsLegacyDecision()
    {
        var coordinator = new AdaptiveScaleCoordinator(
            new AdaptiveScaleOptions { Enabled = false });
        using var frame = Frame();

        var decision = coordinator.EvaluateInitial(
            Recognition(1.0),
            frame,
            MapFeatureCacheSource.CrossResolutionValidated,
            Evidence(),
            openId: 1);

        Assert.True(decision.AllowLegacyCacheWrite);
        Assert.True(decision.AllowReliableSession);
        Assert.Equal("Disabled", decision.Status);
    }

    [Fact]
    public async Task ProductionVpsgEvidenceCanFastConfirmWithTwoStructureFrames()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions
        {
            MinimumObservationSpacingMilliseconds = 50
        });
        using var frame = Frame();
        var recognition = Recognition(1.01);
        var vpsg = new AdaptiveVpsgEvidence(
            true,
            1.0105,
            0.90,
            MapVpsgScaleEstimator.MinimumUniqueMatches,
            MapVpsgScaleEstimator.MinimumPairVotes,
            1.0,
            0.001);
        coordinator.EvaluateInitial(
            recognition,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.04, true, vpsg),
            openId: 1);

        await Task.Delay(60);
        var key = AdaptiveScaleKey.Create(
            recognition.Map,
            recognition.Result.Floor,
            frame.ClientBounds,
            frame.ViewportBounds);
        var decision = EvaluateAndCommit(
            coordinator,
            key,
            1,
            recognition,
            2,
            0.04);

        Assert.True(decision.BecameReliable);
        Assert.Equal(AdaptiveScaleState.Stable, decision.State);
    }

    [Theory]
    [InlineData(MapAlignmentEvidenceKind.Structure, true, false, true)]
    [InlineData(MapAlignmentEvidenceKind.Structure, false, true, true)]
    [InlineData(MapAlignmentEvidenceKind.DualGate, false, false, true)]
    [InlineData(MapAlignmentEvidenceKind.Structure, false, false, false)]
    public async Task InvalidInitialStructureEvidenceDoesNotAddVote(
        MapAlignmentEvidenceKind kind,
        bool reused,
        bool skipped,
        bool structureValidated)
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions
        {
            MinimumObservationSpacingMilliseconds = 50
        });
        using var frame = Frame();
        var invalid = Recognition(1.0, evidenceKind: kind, reused: reused, skipped: skipped);
        coordinator.EvaluateInitial(
            invalid,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.04, structureValidated),
            openId: 1);
        var valid = Recognition(1.0, map: invalid.Map);
        var key = AdaptiveScaleKey.Create(
            valid.Map,
            valid.Result.Floor,
            frame.ClientBounds,
            frame.ViewportBounds);

        await Task.Delay(60);
        Assert.False(EvaluateAndCommit(coordinator, key, 1, valid, 2, 0.04).BecameReliable);
        await Task.Delay(60);
        Assert.False(EvaluateAndCommit(coordinator, key, 1, valid, 3, 0.04).BecameReliable);
        await Task.Delay(60);
        Assert.True(EvaluateAndCommit(coordinator, key, 1, valid, 4, 0.04).BecameReliable);
    }

    [Fact]
    public async Task EffectiveCandidateMarginControlsVoting()
    {
        var coordinator = new AdaptiveScaleCoordinator(new AdaptiveScaleOptions
        {
            MinimumObservationSpacingMilliseconds = 50
        });
        using var frame = Frame();
        var recognition = Recognition(1.0, candidateMargin: 0.05);
        coordinator.EvaluateInitial(
            recognition,
            frame,
            null,
            new AdaptiveScaleInitialEvidence(1, 0.08, true),
            openId: 1);
        var key = AdaptiveScaleKey.Create(
            recognition.Map,
            recognition.Result.Floor,
            frame.ClientBounds,
            frame.ViewportBounds);

        await Task.Delay(60);
        Assert.False(EvaluateAndCommit(coordinator, key, 1, recognition, 2, 0.04).BecameReliable);
        await Task.Delay(60);
        Assert.False(EvaluateAndCommit(coordinator, key, 1, recognition, 3, 0.04).BecameReliable);
        await Task.Delay(60);
        Assert.True(EvaluateAndCommit(coordinator, key, 1, recognition, 4, 0.04).BecameReliable);
    }

    [Fact]
    public async Task ReliableSessionRequiresExactKeyOpenAndRuntimeScale()
    {
        var directory = Directory.CreateTempSubdirectory("idvb-adaptive-scale-");
        try
        {
            using var frame = Frame();
            var recognition = Recognition(1.1);
            var key = AdaptiveScaleKey.Create(
                recognition.Map,
                recognition.Result.Floor,
                frame.ClientBounds,
                frame.ViewportBounds);
            var store = new AdaptiveScaleStore(
                Path.Combine(directory.FullName, "adaptive-scale-cache.json"));
            await store.RecordInitialStreakAsync(Streak(key, 5, 1.1));
            var coordinator = new AdaptiveScaleCoordinator(
                new AdaptiveScaleOptions(),
                store);
            var decision = coordinator.EvaluateInitial(
                recognition,
                frame,
                null,
                Evidence(),
                openId: 7);
            var session = MapAlignmentSession.FromRecognition(
                recognition.Map,
                decision.RecognitionToRender.Result);

            Assert.Equal(AdaptiveScaleReliability.Reliable, decision.Reliability);
            Assert.True(coordinator.CanUseAsReliableSession(session, key, 7));
            Assert.False(coordinator.CanUseAsReliableSession(
                session,
                key with { ViewportWidth = key.ViewportWidth + 1 },
                7));
            Assert.False(coordinator.CanUseAsReliableSession(session, key, 8));
            coordinator.EndOpen(7, "test-close");
            Assert.False(coordinator.CanUseAsReliableSession(session, key, 7));
            await coordinator.DrainAsync();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static CapturedGameFrame Frame() => new(
        new Mat(20, 20, MatType.CV_8UC3, Scalar.Black),
        new MapScreenRect(0, 0, 1920, 1080),
        new MapScreenRect(303, 25, 1314, 1055),
        IntPtr.Zero);

    private static AdaptiveScaleInitialEvidence Evidence(long frameId = 1) =>
        new(frameId, 0.04d, StructureValidated: true);

    private static AdaptiveStructureDecision EvaluateAndCommit(
        AdaptiveScaleCoordinator coordinator,
        AdaptiveScaleKey key,
        long openId,
        RuntimeMapRecognition recognition,
        long frameId,
        double requiredMargin)
    {
        var observed = coordinator.EvaluateStructureObservation(
            key,
            openId,
            recognition,
            frameId,
            requiredMargin);
        return observed.PendingConsensus is { } consensus
            ? coordinator.CommitStructureConsensus(
                key,
                openId,
                recognition,
                consensus,
                requiredMargin)
            : observed;
    }

    private static AdaptiveScaleConsensus Consensus(double scale)
    {
        var observation = new AdaptiveScaleObservation(
            1,
            DateTimeOffset.UtcNow,
            scale,
            0.90,
            0.10,
            AdaptiveScaleObservationSource.Structure,
            Transform(scale));
        return new AdaptiveScaleConsensus(
            scale,
            0.90,
            0.001,
            0.002,
            3,
            0,
            observation);
    }

    private static AdaptiveScaleInitialStreakSnapshot Streak(
        AdaptiveScaleKey key,
        int count,
        double scale)
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, count)
            .Select(_ => new AdaptiveScaleInitialSample(scale, 0.90, now))
            .ToArray();
        return new AdaptiveScaleInitialStreakSnapshot(
            key, samples, count, scale, 0.90, 0d, now);
    }

    private static RuntimeMapRecognition Recognition(
        double scale,
        MapRecord? map = null,
        MapAlignmentEvidenceKind evidenceKind = MapAlignmentEvidenceKind.Structure,
        bool reused = false,
        bool skipped = false,
        double candidateMargin = 0.10) => new()
    {
        Map = map ?? new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Title = "test"
        },
        Result = new MapRecognitionResult
        {
            MapId = Guid.NewGuid(),
            Floor = "1f",
            Confidence = 0.90,
            LocalizationConfidence = 0.90,
            IdentityConfidence = 0.90,
            OverlayTransform = Transform(scale),
            StructureCandidateMargin = candidateMargin,
            StructureRejectionReason = MapStructureRejectionReason.None,
            EvidenceKind = evidenceKind,
            ReusedLastTransform = reused,
            SkippedStructureValidation = skipped
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
