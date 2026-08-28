using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;
public sealed partial class MapSessionModelsTests
{

    [Fact]
    public void ContinuousLockRequiresThreeConsecutiveContradictionsToLose()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var session = MapAlignmentSession.FromRecognition(
            map,
            new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "2f",
                Source = MapRecognitionSource.StructureMatching,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 1d,
                    ScaleY = 1d,
                    ReferenceWidth = 1000,
                    ReferenceHeight = 900,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            });
        var rejection = MapStructureRegistrationResult.Reject(
            MapStructureRejectionReason.NativeScaleChanged);
        var lockedSnapshot = new MapSessionSnapshot
        {
            AlignmentRevision = 1,
            MapId = map.Id,
            Floor = "2f",
            State = MapSessionState.Locked,
            LockedTransform = new MapSimilarityTransform()
        };
        var observation = session.BeginContinuousObservation(map, lockedSnapshot);

        session = session.HoldContinuousObservation(
            map,
            lockedSnapshot,
            observation,
            rejection);
        Assert.False(MapSessionRules.ShouldLoseAlignmentLock(session));
        session = session.HoldContinuousObservation(
            map,
            lockedSnapshot,
            observation,
            rejection);
        Assert.False(MapSessionRules.ShouldLoseAlignmentLock(session));
        session = session.HoldContinuousObservation(
            map,
            lockedSnapshot,
            observation,
            rejection);

        Assert.True(MapSessionRules.ShouldLoseAlignmentLock(session));
        Assert.Equal(3, session.ConsecutiveRejections);
    }

    [Fact]
    public void ContinuousObservationRevisionIgnoresPlayerUpdatesButRejectsRelock()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var recognition = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "2f",
            Source = MapRecognitionSource.StructureMatching,
            Confidence = 0.85d,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = 1d,
                ScaleY = 1d,
                ReferenceWidth = 1000,
                ReferenceHeight = 900,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };
        var alignment = MapAlignmentSession.FromRecognition(map, recognition);
        var openSession = CreateLockedSession(map.Id, "2f");
        var originalLock = openSession.Snapshot;
        var observation = alignment.BeginContinuousObservation(
            map,
            originalLock);

        openSession.UpdatePlayer(null);

        Assert.True(observation.IsCurrent(
            map,
            alignment,
            openSession.Snapshot));
        Assert.NotEqual(originalLock.Version, openSession.Snapshot.Version);
        Assert.Equal(
            originalLock.AlignmentRevision,
            openSession.Snapshot.AlignmentRevision);

        openSession.Close("reopen same map and floor");
        openSession.Transition(MapSessionState.OpeningDetected);
        openSession.Transition(MapSessionState.WaitingForStableFrames);
        openSession.Transition(
            MapSessionState.IdentifyingMap,
            mapId: map.Id,
            floor: "2f");
        openSession.Transition(MapSessionState.CoarseLocating);
        openSession.Transition(MapSessionState.FineLocating);
        openSession.Transition(
            MapSessionState.Locked,
            lockedTransform: new MapSimilarityTransform { Scale = 1d });

        Assert.False(observation.IsCurrent(
            map,
            alignment,
            openSession.Snapshot));
        var held = alignment.HoldContinuousObservation(
            map,
            openSession.Snapshot,
            observation,
            MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.OutsideValidBounds));
        Assert.Equal(0, held.ConsecutiveRejections);
        Assert.Throws<InvalidOperationException>(() =>
            alignment.AdvanceContinuousObservation(
                map,
                recognition,
                openSession.Snapshot,
                observation));
    }

    [Fact]
    public async Task AlignmentCommitGuardSerializesVisualAndLogicalCommit()
    {
        var guard = new MapAlignmentCommitGuard();
        var generation = guard.BeginCommit();
        using var commitEntered = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();

        var commit = Task.Run(() => guard.TryCommit(
            generation,
            () =>
            {
                commitEntered.Set();
                releaseCommit.Wait();
            }));
        Assert.True(commitEntered.Wait(TimeSpan.FromSeconds(10)));

        var newerGeneration = Task.Run(guard.BeginCommit);
        await Task.Delay(50);
        Assert.False(newerGeneration.IsCompleted);

        releaseCommit.Set();
        Assert.True(await commit);
        var next = await newerGeneration;
        Assert.True(next > generation);
        Assert.True(guard.IsCurrent(next));
    }
}
