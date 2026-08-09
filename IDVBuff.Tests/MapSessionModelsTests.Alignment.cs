using IDVBuff.Features.Maps;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class MapSessionModelsTests
{
    [Fact]
    public void PhysicalGeometryChangesRequireRecalibrationButDpiAloneDoesNot()
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
            MapRecalibrationReason.None,
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
    public void SideEntranceSessionAdvanceRefreshesBaselineGateScale()
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
                Confidence = 0.9d,
                Source = MapRecognitionSource.SideEntranceSelection,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 1.2d,
                    ScaleY = 1.2d,
                    ReferenceWidth = 1000,
                    ReferenceHeight = 900,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                },
                AnchorMatches = []
            });

        Assert.Equal(1.2d, session.BaselineGateScale);
        Assert.True(session.SideEntranceScanPriorConfidence > 0d);

        var tracked = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "1f",
            Confidence = 0.82d,
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = 1.206d,
                ScaleY = 1.206d,
                OffsetX = 12d,
                OffsetY = -8d,
                ReferenceWidth = 1000,
                ReferenceHeight = 900,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };

        var advanced = session.Advance(map, tracked);

        // 侧门路径下缩放基线跟随最新结构配准结果，避免与 LockedTransform 累积
        // 分叉，从而不再把高质量结构配准误判为"超过安全范围的地图缩放"。
        Assert.Equal(1.206d, advanced.LockedTransform.ScaleX, 8);
        Assert.Equal(1.206d, advanced.BaselineGateScale, 8);
    }

    [Fact]
    public void DualGateSessionAdvanceRetainsBaselineGateScale()
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
                Confidence = 0.9d,
                Source = MapRecognitionSource.StructureMatching,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 1.2d,
                    ScaleY = 1.2d,
                    ReferenceWidth = 1000,
                    ReferenceHeight = 900,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                },
                AnchorMatches = []
            });

        Assert.False(session.SideEntranceScanPriorConfidence > 0d);

        var tracked = new MapRecognitionResult
        {
            MapId = map.Id,
            Floor = "1f",
            Confidence = 0.82d,
            Source = MapRecognitionSource.StructureMatching,
            OverlayTransform = new MapOverlayTransform
            {
                ScaleX = 1.21d,
                ScaleY = 1.21d,
                OffsetX = 12d,
                OffsetY = -8d,
                ReferenceWidth = 1000,
                ReferenceHeight = 900,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            }
        };

        var advanced = session.Advance(map, tracked);

        // 双门会话仍保留原始缩放基线，用于检测真实地图缩放变化。
        Assert.Equal(1.2d, advanced.BaselineGateScale);
        Assert.Equal(1.21d, advanced.LockedTransform.ScaleX, 8);
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
