using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;
public sealed partial class MapSessionModelsTests
{

    [Fact]
    public void PassiveFloorChangeStreakResetsOnNoiseOrCurrentFloor()
    {
        var tracker = new MapFloorChangeTracker();

        Assert.False(tracker.Observe("1f", "2f"));
        Assert.False(tracker.Observe("1f", null));
        Assert.False(tracker.Observe("1f", "2f"));
        Assert.False(tracker.Observe("1f", "basement"));
        Assert.False(tracker.Observe("1f", "1f"));
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.CandidateFloor);
    }

    [Fact]
    public void AlignmentCommitGuardRejectsSupersededOrInvalidatedRender()
    {
        var guard = new MapAlignmentCommitGuard();
        var first = guard.BeginCommit();
        var second = guard.BeginCommit();

        Assert.False(guard.IsCurrent(first));
        Assert.True(guard.IsCurrent(second));

        guard.Invalidate();

        Assert.False(guard.IsCurrent(second));
    }

    [Fact]
    public void StaleCommitCannotInvalidateNewerRender()
    {
        var guard = new MapAlignmentCommitGuard();
        var stale = guard.BeginCommit();
        var current = guard.BeginCommit();

        Assert.False(guard.TryInvalidate(stale));
        Assert.True(guard.IsCurrent(current));
        Assert.True(guard.TryInvalidate(current));
        Assert.False(guard.IsCurrent(current));
    }

    [Fact]
    public void OnlyCurrentRenderCanCommitLogicalLockState()
    {
        var guard = new MapAlignmentCommitGuard();
        var stale = guard.BeginCommit();
        var current = guard.BeginCommit();
        var committedGeneration = 0L;

        Assert.False(guard.TryCommit(
            stale,
            () => committedGeneration = stale));
        Assert.Equal(0L, committedGeneration);

        Assert.True(guard.TryCommit(
            current,
            () => committedGeneration = current));
        Assert.Equal(current, committedGeneration);
    }

    [Fact]
    public void UserSelectionLocksIdentityBeforeAlignmentTransformExists()
    {
        var mapId = Guid.NewGuid();
        var session = new MapOpenSession();

        var identityLocked = session.LockMapIdentity(
            mapId,
            "1f",
            confidence: 1d);

        Assert.True(identityLocked.IsIdentityLocked);
        Assert.False(identityLocked.IsLocked);
        Assert.Equal(MapSessionState.Confirming, identityLocked.State);
        Assert.Equal(mapId, identityLocked.MapId);
        Assert.Equal("1f", identityLocked.Floor);
        Assert.Null(identityLocked.LockedTransform);
    }

    [Fact]
    public void AcceptedAlignmentPromotesUserIdentityLockToTransformLock()
    {
        var mapId = Guid.NewGuid();
        var session = new MapOpenSession();
        session.LockMapIdentity(mapId, "1f", confidence: 1d);
        var transform = new MapSimilarityTransform
        {
            Scale = 1d,
            RotationDegrees = 0d,
            TranslationX = 12d,
            TranslationY = 34d
        };

        var aligned = session.LockAlignedMap(
            mapId,
            "1f",
            transform,
            MapLocationMethod.StructureTranslation,
            confidence: 0.9d);

        Assert.True(aligned.IsIdentityLocked);
        Assert.True(aligned.IsLocked);
        Assert.Equal(MapSessionState.Locked, aligned.State);
        Assert.Equal(transform, aligned.LockedTransform);
    }

    [Fact]
    public void ConfidenceOmitsUnavailableEvidenceFromDenominator()
    {
        var confidence = new MapRegistrationConfidenceEvidence
        {
            AnchorGeometry = 0.9d,
            StructureQuality = 0.7d
        }.Calculate();

        Assert.Equal(
            ((0.9d * 0.20d) + (0.7d * 0.25d)) / 0.45d,
            confidence,
            precision: 8);
    }
}
