using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapSessionModelsTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(90d)]
    [InlineData(270d)]
    public void SimilarityTransformRoundTripsReferenceCoordinates(
        double rotation)
    {
        var transform = new MapSimilarityTransform
        {
            Scale = 1.35d,
            RotationDegrees = rotation,
            TranslationX = 730d,
            TranslationY = 215d
        };
        var reference = new MapReferencePoint(187.25d, 93.75d);

        var roundTrip = transform.ToReference(
            transform.ToScreen(reference));

        Assert.InRange(Math.Abs(roundTrip.X - reference.X), 0d, 0.000001d);
        Assert.InRange(Math.Abs(roundTrip.Y - reference.Y), 0d, 0.000001d);
    }

    [Theory]
    [InlineData(-50d, -30d, 0d, 0d)]
    [InlineData(0d, 0d, 0d, 0d)]
    [InlineData(350d, -30d, 350d, 0d)]
    [InlineData(-50d, 250d, 0d, 250d)]
    [InlineData(350d, 250d, 350d, 250d)]
    [InlineData(900d, 250d, 700d, 250d)]
    [InlineData(350d, 700d, 350d, 500d)]
    [InlineData(-50d, 700d, 0d, 500d)]
    [InlineData(900d, -30d, 700d, 0d)]
    [InlineData(900d, 700d, 700d, 500d)]
    public void ViewportOriginIsConstrainedAtEveryMapBoundary(
        double inputX,
        double inputY,
        double expectedX,
        double expectedY)
    {
        var bounds = new MapReferenceBounds
        {
            Width = 1000d,
            Height = 800d
        };

        var actual = bounds.ClampViewportOrigin(
            new MapViewportOrigin(inputX, inputY),
            viewportWidth: 300d,
            viewportHeight: 300d);

        Assert.Equal(expectedX, actual.X);
        Assert.Equal(expectedY, actual.Y);
    }

    [Theory]
    [InlineData(-300d, -250d, -200d, -200d)]
    [InlineData(-100d, -100d, -100d, -100d)]
    [InlineData(100d, 100d, 0d, 0d)]
    public void OversizedViewportCanSurroundTheReferenceMap(
        double inputX,
        double inputY,
        double expectedX,
        double expectedY)
    {
        var bounds = new MapReferenceBounds
        {
            Width = 1000d,
            Height = 800d
        };

        var actual = bounds.ClampViewportOrigin(
            new MapViewportOrigin(inputX, inputY),
            viewportWidth: 1200d,
            viewportHeight: 1000d);

        Assert.Equal(expectedX, actual.X);
        Assert.Equal(expectedY, actual.Y);
    }

    [Fact]
    public void SessionFollowsOpenStableLocateConfirmLockCloseLifecycle()
    {
        var session = new MapOpenSession();
        var mapId = Guid.NewGuid();
        var transform = new MapSimilarityTransform
        {
            Scale = 1.1d,
            TranslationX = 400d,
            TranslationY = 200d
        };

        session.Transition(MapSessionState.OpeningDetected);
        session.Transition(MapSessionState.WaitingForStableFrames);
        session.Transition(
            MapSessionState.IdentifyingMap,
            mapId: mapId,
            floor: "1f");
        session.Transition(MapSessionState.CoarseLocating);
        session.Transition(MapSessionState.FineLocating);
        session.Transition(MapSessionState.Confirming);
        var locked = session.Transition(
            MapSessionState.Locked,
            viewportOrigin: new MapViewportOrigin(50d, 80d),
            lockedTransform: transform,
            confidence: 0.84d);

        Assert.True(locked.IsLocked);
        Assert.Same(transform, locked.LockedTransform);

        var closed = session.Close("closed");

        Assert.Equal(MapSessionState.Closed, closed.State);
        Assert.Null(closed.MapId);
        Assert.Null(closed.ViewportOrigin);
        Assert.Null(closed.LockedTransform);
        Assert.Null(closed.Player);
    }

    [Theory]
    [InlineData(MapSessionState.Closed, false)]
    [InlineData(MapSessionState.OpeningDetected, true)]
    [InlineData(MapSessionState.WaitingForStableFrames, true)]
    [InlineData(MapSessionState.LowConfidence, true)]
    [InlineData(MapSessionState.Locked, true)]
    [InlineData(MapSessionState.Lost, true)]
    public void PassiveVisualPresenceCannotStartAClosedSession(
        MapSessionState state,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapSessionRules.ShouldMonitorVisualPresence(state));
    }

    [Theory]
    [InlineData(MapSessionState.Locked, true, false)]
    [InlineData(MapSessionState.Locked, false, true)]
    [InlineData(MapSessionState.FineLocating, false, false)]
    [InlineData(MapSessionState.LowConfidence, false, false)]
    [InlineData(MapSessionState.Closed, false, false)]
    public void ExplicitRecognitionSuspendsPassiveSessionMonitoring(
        MapSessionState state,
        bool scanInProgress,
        bool expected)
    {
        Assert.Equal(
            expected,
            MapSessionRules.ShouldRunPassiveSessionMonitor(
                state,
                scanInProgress));
    }

    [Fact]
    public void LowConfidenceSessionCannotClaimAnotherAutomaticPipeline()
    {
        var toggle = new MapGameToggleState();
        var opened = toggle.Toggle();
        var session = new MapOpenSession();
        session.Transition(MapSessionState.OpeningDetected);
        session.Transition(MapSessionState.WaitingForStableFrames);
        session.Transition(MapSessionState.LowConfidence);

        Assert.True(toggle.TryBeginOpenPipeline(opened));
        for (var attempt = 0; attempt < 100; attempt++)
            Assert.False(toggle.TryBeginOpenPipeline(opened));
        Assert.Equal(MapSessionState.LowConfidence, session.Snapshot.State);
    }

    [Fact]
    public void ClosedSessionRejectsStaleOpenContinuation()
    {
        var toggle = new MapGameToggleState();
        var opened = toggle.Toggle();
        Assert.True(toggle.TryBeginOpenPipeline(opened));
        var session = new MapOpenSession();
        session.Transition(MapSessionState.OpeningDetected);

        var closed = toggle.Toggle();
        session.Close("map closed");

        Assert.False(closed.IsOpen);
        Assert.False(
            MapSessionRules.CanContinueOpenPipeline(
                toggle,
                opened,
                session.Snapshot.State));
    }

    [Fact]
    public void PlayerMovementCannotChangeLockedBackground()
    {
        var session = CreateLockedSession();
        var before = session.Snapshot.LockedTransform;

        session.UpdatePlayer(new MapPlayerState
        {
            PlayerSlot = PlayerSlot.Player2,
            ViewportPoint = new MapViewportPoint(100d, 120d),
            ScreenPoint = new MapScreenPoint(800d, 500d),
            ReferencePoint = new MapReferencePoint(220d, 190d),
            MarkerWidth = 48d,
            MarkerHeight = 48d,
            Confidence = 0.92d,
            ObservedAt = DateTimeOffset.UtcNow
        });
        session.UpdatePlayer(new MapPlayerState
        {
            PlayerSlot = PlayerSlot.Player2,
            ViewportPoint = new MapViewportPoint(180d, 210d),
            ScreenPoint = new MapScreenPoint(880d, 590d),
            ReferencePoint = new MapReferencePoint(300d, 280d),
            MarkerWidth = 48d,
            MarkerHeight = 48d,
            Confidence = 0.91d,
            ObservedAt = DateTimeOffset.UtcNow
        });

        Assert.Same(before, session.Snapshot.LockedTransform);
        Assert.Equal(MapSessionState.Locked, session.Snapshot.State);
        Assert.Equal(300d, session.Snapshot.Player!.ReferencePoint.X);
        Assert.True(session.Snapshot.Player.IsTrusted);
        Assert.Equal(PlayerSlot.Player2, session.Snapshot.Player.PlayerSlot);
    }

    [Fact]
    public void TrustedAlignmentObservationUpdatesLockAndPreservesPlayer()
    {
        var session = new MapOpenSession();
        var mapId = Guid.NewGuid();
        session.Transition(MapSessionState.OpeningDetected);
        session.Transition(MapSessionState.WaitingForStableFrames);
        session.Transition(
            MapSessionState.IdentifyingMap,
            mapId: mapId,
            floor: "2f");
        session.Transition(MapSessionState.CoarseLocating);
        session.Transition(MapSessionState.FineLocating);
        session.Transition(
            MapSessionState.Locked,
            lockedTransform: new MapSimilarityTransform { Scale = 1d },
            confidence: 0.70d);
        var initialAlignmentRevision = session.Snapshot.AlignmentRevision;
        Assert.True(initialAlignmentRevision > 0);
        var player = new MapPlayerState
        {
            PlayerSlot = PlayerSlot.Player1,
            ViewportPoint = new MapViewportPoint(20d, 30d),
            ScreenPoint = new MapScreenPoint(120d, 130d),
            ReferencePoint = new MapReferencePoint(220d, 230d),
            MarkerWidth = 20d,
            MarkerHeight = 20d,
            Confidence = 0.90d
        };
        session.UpdatePlayer(player);
        Assert.Equal(
            initialAlignmentRevision,
            session.Snapshot.AlignmentRevision);
        var updatedTransform = new MapSimilarityTransform
        {
            Scale = 1.01d,
            TranslationX = 105d,
            TranslationY = 205d
        };

        var updated = session.UpdateLockedAlignment(
            mapId,
            "2f",
            MapLocationMethod.StructureTranslation,
            new MapViewportOrigin(40d, 50d),
            updatedTransform,
            0.76d,
            3,
            "continuous observation");

        Assert.Same(updatedTransform, updated.LockedTransform);
        Assert.Same(player, updated.Player);
        Assert.Equal(0.76d, updated.Confidence);
        Assert.Equal(MapSessionState.Locked, updated.State);
        Assert.Equal(MapLocationMethod.StructureTranslation, updated.LocationMethod);
        Assert.Equal(3, updated.StableCandidateFrames);
        Assert.True(updated.AlignmentRevision > initialAlignmentRevision);
    }

    [Fact]
    public void TrustedAlignmentObservationCannotChangeMapOrFloor()
    {
        var session = new MapOpenSession();
        var mapId = Guid.NewGuid();
        session.Transition(MapSessionState.OpeningDetected);
        session.Transition(MapSessionState.WaitingForStableFrames);
        session.Transition(
            MapSessionState.IdentifyingMap,
            mapId: mapId,
            floor: "1f");
        session.Transition(MapSessionState.CoarseLocating);
        session.Transition(MapSessionState.FineLocating);
        session.Transition(
            MapSessionState.Locked,
            lockedTransform: new MapSimilarityTransform { Scale = 1d });

        Assert.Throws<InvalidOperationException>(() =>
            session.UpdateLockedAlignment(
                Guid.NewGuid(),
                "1f",
                MapLocationMethod.StructureTranslation,
                new MapViewportOrigin(),
                new MapSimilarityTransform { Scale = 1d },
                0.8d,
                1));
        Assert.Throws<InvalidOperationException>(() =>
            session.UpdateLockedAlignment(
                mapId,
                "2f",
                MapLocationMethod.StructureTranslation,
                new MapViewportOrigin(),
                new MapSimilarityTransform { Scale = 1d },
                0.8d,
                1));
    }

    [Fact]
    public void LockedTransitionCannotReplaceFloorWithoutRecalibration()
    {
        var session = CreateLockedSession();

        Assert.Throws<InvalidOperationException>(() =>
            session.Transition(
                MapSessionState.Locked,
                floor: "2f",
                lockedTransform: new MapSimilarityTransform { Scale = 1d }));
    }

    [Fact]
    public void PlayerDoesNotSurviveRecalibrationAndFreshFloorLock()
    {
        var session = CreateLockedSession();
        session.UpdatePlayer(new MapPlayerState
        {
            PlayerSlot = PlayerSlot.Player1,
            ViewportPoint = new MapViewportPoint(20d, 30d),
            ScreenPoint = new MapScreenPoint(120d, 130d),
            ReferencePoint = new MapReferencePoint(220d, 230d),
            MarkerWidth = 20d,
            MarkerHeight = 20d,
            Confidence = 0.90d
        });

        var recalibrating = session.Transition(
            MapSessionState.RecalibrationRequired,
            floor: "2f",
            reason: MapRecalibrationReason.FloorChanged);
        session.Transition(MapSessionState.CoarseLocating);
        session.Transition(MapSessionState.FineLocating);
        var relocked = session.Transition(
            MapSessionState.Locked,
            floor: "2f",
            lockedTransform: new MapSimilarityTransform { Scale = 1d });

        Assert.Null(recalibrating.Player);
        Assert.Null(relocked.Player);
        Assert.Equal("2f", relocked.Floor);
    }

    [Fact]
    public void FreshLockCannotResurrectTransformFromRecalibrationState()
    {
        var session = CreateLockedSession();
        session.Transition(
            MapSessionState.RecalibrationRequired,
            reason: MapRecalibrationReason.AlignmentLost);
        session.Transition(MapSessionState.CoarseLocating);
        session.Transition(MapSessionState.FineLocating);

        Assert.Null(session.Snapshot.LockedTransform);
        Assert.Throws<InvalidOperationException>(() =>
            session.Transition(MapSessionState.Locked));
    }

    [Fact]
    public void PlayerReferencePointIsReprojectedWithTrustedTransformUpdate()
    {
        var player = new MapPlayerState
        {
            PlayerSlot = PlayerSlot.Player2,
            ViewportPoint = new MapViewportPoint(40d, 50d),
            ScreenPoint = new MapScreenPoint(240d, 350d),
            ReferencePoint = new MapReferencePoint(140d, 250d),
            MarkerWidth = 20d,
            MarkerHeight = 20d,
            Confidence = 0.90d,
            ObservedAt = DateTimeOffset.UtcNow
        };
        var transform = new MapSimilarityTransform
        {
            Scale = 1d,
            TranslationX = 80d,
            TranslationY = 120d
        };

        var reprojected = MapSessionRules.ReprojectPlayer(
            player,
            transform,
            new MapReferenceBounds
            {
                X = 0d,
                Y = 0d,
                Width = 1000d,
                Height = 1000d
            });

        Assert.NotNull(reprojected);
        Assert.Equal(160d, reprojected.ReferencePoint.X, 8);
        Assert.Equal(230d, reprojected.ReferencePoint.Y, 8);
        Assert.Equal(player.ScreenPoint, reprojected.ScreenPoint);
        Assert.Equal(player.ObservedAt, reprojected.ObservedAt);
    }

    [Fact]
    public void MediumConfidenceCandidateRequiresThreeStableFrames()
    {
        var tracker = new MapCandidateStabilityTracker();
        var first = new MapSimilarityTransform
        {
            Scale = 1d,
            TranslationX = 100d,
            TranslationY = 200d
        };

        Assert.False(tracker.Observe(first));
        Assert.False(tracker.Observe(new MapSimilarityTransform
        {
            Scale = 1d,
            TranslationX = 102d,
            TranslationY = 198d
        }));
        Assert.True(tracker.Observe(new MapSimilarityTransform
        {
            Scale = 1d,
            TranslationX = 101d,
            TranslationY = 199d
        }));
        Assert.Equal(3, tracker.History.Count);

        Assert.False(tracker.Observe(new MapSimilarityTransform
        {
            Scale = 1d,
            TranslationX = 120d,
            TranslationY = 199d
        }));
        Assert.Equal(1, tracker.Count);
        Assert.Single(tracker.History);
    }

    [Theory]
    [InlineData(0.82d, false, 0, true)]
    [InlineData(0.70d, false, 2, false)]
    [InlineData(0.70d, false, 3, true)]
    [InlineData(0.70d, true, 0, true)]
    public void LockStabilityUsesActualObservedFrames(
        double confidence,
        bool skipConfirmation,
        int observedFrames,
        bool expected)
    {
        var accepted = MapSessionRules.HasRequiredLockStability(
            confidence,
            highConfidence: 0.82d,
            skipConfirmation,
            observedFrames,
            requiredStableFrames: 3);

        Assert.Equal(expected, accepted);
    }

    [Fact]
    public void PassiveFloorChangeRequiresConsecutiveMatchingObservations()
    {
        var tracker = new MapFloorChangeTracker();

        Assert.False(tracker.Observe("1f", "2f"));
        Assert.False(tracker.Observe("1f", "2f"));
        Assert.True(tracker.Observe("1f", "2f"));
        Assert.Equal(3, tracker.Count);
        Assert.Equal("2f", tracker.CandidateFloor);
    }

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
