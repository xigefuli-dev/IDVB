using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapSessionModelsTests
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

    [Fact]
    public void WindowSignatureChangesWhenPositionResolutionViewportOrDpiChanges()
    {
        var baselineProfile = DisplayTestMatrix.Baseline;
        var baseline = baselineProfile.CreateSignature();
        var differentScale = DisplayTestMatrix.Profiles.First(profile =>
            profile.PixelWidth == baselineProfile.PixelWidth
            && profile.PixelHeight == baselineProfile.PixelHeight
            && profile.Dpi != baselineProfile.Dpi);
        var differentResolution = DisplayTestMatrix.Profiles.First(profile =>
            profile.Dpi == baselineProfile.Dpi
            && (profile.PixelWidth != baselineProfile.PixelWidth
                || profile.PixelHeight != baselineProfile.PixelHeight));

        Assert.Equal(
            baseline,
            baselineProfile.CreateSignature());
        Assert.NotEqual(
            baseline,
            baselineProfile.CreateSignature(clientX: 101));
        Assert.NotEqual(
            baseline,
            differentResolution.CreateSignature());

        Assert.Equal(
            MapRecalibrationReason.DpiChanged,
            MapSessionRules.GetSignatureChangeReason(
                baseline,
                differentScale.CreateSignature()));
        Assert.Equal(
            MapRecalibrationReason.ResolutionChanged,
            MapSessionRules.GetSignatureChangeReason(
                baseline,
                differentResolution.CreateSignature()));
        Assert.Equal(
            MapRecalibrationReason.ViewportChanged,
            MapSessionRules.GetSignatureChangeReason(
                baseline,
                CopySignature(baseline, viewportWidth: 1700)));
        Assert.Equal(
            MapRecalibrationReason.WindowChanged,
            MapSessionRules.GetSignatureChangeReason(
                baseline,
                CopySignature(baseline, clientX: 101)));
    }

    private static MapWindowSignature CopySignature(
        MapWindowSignature source,
        int? clientX = null,
        int? clientWidth = null,
        int? viewportWidth = null,
        uint? dpi = null) =>
        new()
        {
            WindowHandle = source.WindowHandle,
            ClientX = clientX ?? source.ClientX,
            ClientY = source.ClientY,
            ClientWidth = clientWidth ?? source.ClientWidth,
            ClientHeight = source.ClientHeight,
            ViewportX = source.ViewportX,
            ViewportY = source.ViewportY,
            ViewportWidth = viewportWidth ?? source.ViewportWidth,
            ViewportHeight = source.ViewportHeight,
            Dpi = dpi ?? source.Dpi
        };

    private static MapOpenSession CreateLockedSession(
        Guid? mapId = null,
        string floor = "1f")
    {
        var session = new MapOpenSession();
        session.Transition(MapSessionState.OpeningDetected);
        session.Transition(MapSessionState.WaitingForStableFrames);
        session.Transition(
            MapSessionState.IdentifyingMap,
            mapId: mapId,
            floor: floor);
        session.Transition(MapSessionState.CoarseLocating);
        session.Transition(MapSessionState.FineLocating);
        session.Transition(
            MapSessionState.Locked,
            lockedTransform: new MapSimilarityTransform
            {
                Scale = 1d,
                TranslationX = 600d,
                TranslationY = 300d
            },
            confidence: 0.9d);
        return session;
    }

    [Fact]
    public void SecondFloorCalibrationDoesNotMatchFirstFloorSignature()
    {
        var display = DisplayTestMatrix.Baseline;
        var mapId = Guid.NewGuid();
        var mapUpdatedAt = DateTimeOffset.UtcNow;
        var signature = display.CreateSignature(
            windowHandle: 1,
            clientX: 0,
            clientY: 0);
        var calibration = new MapAlignmentCalibration
        {
            MapId = mapId,
            Floor = "2f",
            MapUpdatedAt = mapUpdatedAt,
            ReferenceWidth = 1600,
            ReferenceHeight = 1200,
            UniformScale = 1.25d,
            RotationDegrees = 0d,
            ClientWidth = signature.ClientWidth,
            ClientHeight = signature.ClientHeight,
            ViewportWidth = signature.ViewportWidth,
            ViewportHeight = signature.ViewportHeight,
            Dpi = signature.Dpi,
            Confidence = 0.91d,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Assert.False(calibration.Matches(
            mapId,
            mapUpdatedAt,
            signature,
            "1f"));
        Assert.True(calibration.Matches(
            mapId,
            mapUpdatedAt,
            signature,
            "2f"));
    }

    [Fact]
    public void StructureMatchingRecognitionYieldsStructureMatchedSession()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var transform = new MapOverlayTransform
        {
            ScaleX = 1.2d,
            ScaleY = 1.2d,
            OffsetX = 400d,
            OffsetY = 200d,
            ReferenceWidth = 1000,
            ReferenceHeight = 900,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        var session = MapAlignmentSession.FromRecognition(
            map,
            new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "2f",
                Confidence = 0.88d,
                Source = MapRecognitionSource.StructureMatching,
                OverlayTransform = transform,
                AnchorMatches = []
            });

        Assert.Equal(MapAlignmentTrackingMode.StructureMatched, session.Mode);
        Assert.Null(session.GateTemplateScale);
        Assert.Equal(1.2d, session.BaselineGateScale);
        Assert.Same(transform, session.LockedTransform);
    }

    [Fact]
    public void ContinuousObservationCannotDriftBeyondBaselineScaleLock()
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
                Floor = "1f",
                Source = MapRecognitionSource.Automatic,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 1d,
                    ScaleY = 1d,
                    ReferenceWidth = 1000,
                    ReferenceHeight = 900,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            });
        var drifted = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "1f",
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = 1.04d,
                ScaleY = 1.04d,
                ReferenceWidth = 1000,
                ReferenceHeight = 900,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            session.Advance(map, drifted, maximumScaleChangeRatio: 0.03d));
    }

    [Fact]
    public void ContinuousObservationWithinBaselineScaleLockCanAdvance()
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
        var observation = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "2f",
            Source = MapRecognitionSource.StructureMatching,
            Confidence = 0.75d,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = 1.02d,
                ScaleY = 1.02d,
                OffsetX = 12d,
                OffsetY = -8d,
                ReferenceWidth = 1000,
                ReferenceHeight = 900,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };

        var advanced = session.Advance(
            map,
            observation,
            maximumScaleChangeRatio: 0.03d);

        Assert.Equal(1.02d, advanced.LockedTransform.ScaleX, 8);
        Assert.Equal(0, advanced.ConsecutiveRejections);
        Assert.Equal(0.75d, advanced.LastConfidence, 8);
    }

    [Fact]
    public void ContinuousLockCountsOnlyIdentityMatchedContradictions()
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
                Confidence = 0.85d,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 1d,
                    ScaleY = 1d,
                    ReferenceWidth = 1000,
                    ReferenceHeight = 900,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            });
        var lockedSnapshot = new MapSessionSnapshot
        {
            AlignmentRevision = 1,
            MapId = map.Id,
            Floor = "2f",
            State = MapSessionState.Locked,
            LockedTransform = new MapSimilarityTransform()
        };
        var observation = session.BeginContinuousObservation(map, lockedSnapshot);

        var inconclusive = session.HoldContinuousObservation(
            map,
            lockedSnapshot,
            observation,
            MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.AmbiguousCandidates));
        var contradictory = inconclusive.HoldContinuousObservation(
            map,
            lockedSnapshot,
            observation,
            MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.NativeScaleChanged));
        var wrongFloor = contradictory.HoldContinuousObservation(
            map,
            new MapSessionSnapshot
            {
                AlignmentRevision = lockedSnapshot.AlignmentRevision,
                MapId = map.Id,
                Floor = "1f",
                State = MapSessionState.Locked,
                LockedTransform = lockedSnapshot.LockedTransform
            },
            observation,
            MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.NativeScaleChanged));
        var unlocked = contradictory.HoldContinuousObservation(
            map,
            new MapSessionSnapshot
            {
                AlignmentRevision = lockedSnapshot.AlignmentRevision,
                MapId = map.Id,
                Floor = "2f",
                State = MapSessionState.LowConfidence,
                LockedTransform = lockedSnapshot.LockedTransform
            },
            observation,
            MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.NativeScaleChanged));

        Assert.Equal(0, inconclusive.ConsecutiveRejections);
        Assert.Equal(1, contradictory.ConsecutiveRejections);
        Assert.Equal(0, wrongFloor.ConsecutiveRejections);
        Assert.Equal(0, unlocked.ConsecutiveRejections);
        Assert.Same(session.LockedTransform, wrongFloor.LockedTransform);
    }

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
