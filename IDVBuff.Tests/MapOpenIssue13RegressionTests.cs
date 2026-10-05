using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "13")]
public sealed class MapOpenIssue13RegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedIdentityKeepsCaptureAndFirstAlignmentAliveEvenWhenAlignmentIsRejected(bool accepted)
    {
        var owner = new MapOpenCancellationOwner();
        var scope = owner.Begin(default, default);
        var session = new MapOpenSession();
        var mapId = Guid.NewGuid();
        var transform = new MapSimilarityTransform { Scale = 1.25, TranslationX = 12, TranslationY = 34 };
        try
        {
            owner.CancelUnlessOwnedBy(scope.Token);
            session.LockMapIdentity(mapId, "1f", 1);
            Assert.True(session.Snapshot.IsIdentityLocked);
            Assert.False(session.Snapshot.IsLocked);
            Assert.Null(session.Snapshot.LockedTransform);

            var captured = 0;
            var aligned = 0;
            MapSimilarityTransform? alignedTransform = null;
            await MapOperationContinuation.CommitAfterAsync(async () =>
            {
                alignedTransform = await NoDoorAlignmentDeadline.RunAsync<MapSimilarityTransform?>(() =>
                {
                    scope.Token.ThrowIfCancellationRequested();
                    captured++;
                    Assert.True(NoDoorAlignmentDeadline.Current!.CanStartStage());
                    aligned++;
                    return accepted ? transform : null;
                }, scope.Token);
            }, () => alignedTransform is not null
                ? session.LockAlignedMap(mapId, "1f", alignedTransform, MapLocationMethod.StructureTranslation, .9)
                : session.Snapshot, scope.Token,
                () => session.Snapshot.MapId == mapId && session.Snapshot.Floor == "1f");

            Assert.Equal(1, captured);
            Assert.Equal(1, aligned);
            Assert.False(scope.IsCancellationRequested);
            Assert.True(session.Snapshot.IsIdentityLocked);
            Assert.Equal(mapId, session.Snapshot.MapId);
            Assert.Equal("1f", session.Snapshot.Floor);
            Assert.Equal(accepted, session.Snapshot.IsLocked);
            if (accepted) Assert.Same(transform, session.Snapshot.LockedTransform);
            else Assert.Null(session.Snapshot.LockedTransform);
        }
        finally { owner.Complete(scope); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnavailableOperationDoesNotEvenStartCaptureOrReset(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var prepared = 0;
        var committed = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MapOperationContinuation.CommitAfterAsync(() =>
            {
                prepared++;
                return Task.CompletedTask;
            }, () => ++committed, cancellation.Token, () => cancelled));

        Assert.Equal(0, prepared);
        Assert.Equal(0, committed);
    }

    [Fact]
    public async Task FailedPreparationCannotCommitAndPreservesTheActualFailure()
    {
        var failure = new InvalidOperationException("capture or scale reset failed");
        var committed = 0;
        var seen = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MapOperationContinuation.CommitAfterAsync(() => Task.FromException(failure),
                () => ++committed, default, () => true));
        Assert.Same(failure, seen);
        Assert.Equal(0, committed);
    }

    [Fact]
    public async Task OldCaptureFinallyCannotUndoTheNewOwnersAlignedMap()
    {
        var owner = new MapOpenCancellationOwner();
        var session = new MapOpenSession();
        var oldMap = Guid.NewGuid();
        var newMap = Guid.NewGuid();
        var oldScope = owner.Begin(default, default);
        var oldToken = oldScope.Token;
        session.LockMapIdentity(oldMap, "1f", 1);
        var oldCaptureReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWrites = 0;
        var newTransform = new MapSimilarityTransform { Scale = 1.8, TranslationX = 82, TranslationY = 96 };
        var pendingOld = MapOperationContinuation.CommitAfterAsync(async () =>
        {
            oldCaptureReady.SetResult();
            await releaseOldCapture.Task;
        }, () =>
        {
            oldWrites++;
            return session.LockAlignedMap(oldMap, "1f", new MapSimilarityTransform { Scale = .7 },
                MapLocationMethod.StructureTranslation, .9);
        }, oldToken, () => session.Snapshot.MapId == oldMap);
        CancellationTokenSource? currentScope = null;
        var oldCompleted = false;
        try
        {
            await oldCaptureReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            currentScope = owner.Begin(default, default);
            owner.CancelUnlessOwnedBy(currentScope.Token);
            session.LockMapIdentity(newMap, "2f", 1);
            var currentSnapshot = await MapOperationContinuation.CommitAfterAsync(() => Task.CompletedTask,
                () => session.LockAlignedMap(newMap, "2f", newTransform,
                    MapLocationMethod.StructureTranslation, .95), currentScope.Token);

            owner.Complete(oldScope);
            oldCompleted = true;
            owner.CancelUnlessOwnedBy(currentScope.Token);
            releaseOldCapture.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingOld);

            Assert.Equal(0, oldWrites);
            Assert.False(currentScope.IsCancellationRequested);
            Assert.Same(currentSnapshot, session.Snapshot);
            Assert.Equal(newMap, session.Snapshot.MapId);
            Assert.Equal("2f", session.Snapshot.Floor);
            Assert.Same(newTransform, session.Snapshot.LockedTransform);
        }
        finally
        {
            releaseOldCapture.TrySetResult();
            try { await pendingOld; } catch (OperationCanceledException) { }
            if (!oldCompleted) owner.Complete(oldScope);
            if (currentScope is not null) owner.Complete(currentScope);
        }
    }
}
