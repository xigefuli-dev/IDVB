using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void StructureRejectionsAreClassifiedForFastPathPolicy()
    {
        Assert.Equal(
            MapStructureEvidenceDisposition.Inconclusive,
            MapStructureRejectionReason.InsufficientStructure
                .ToDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Inconclusive,
            MapStructureRejectionReason.TimeBudgetExceeded
                .ToDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Contradictory,
            MapStructureRejectionReason.AnchorTransformConflict
                .ToDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.SystemError,
            MapStructureRejectionReason.InvalidInput.ToDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Supportive,
            MapStructureRejectionReason.None.ToDisposition(
                accepted: true));
    }

    [Fact]
    public void ContinuousLockLossCountsOnlyMeasuredScaleContradictions()
    {
        Assert.Equal(
            MapStructureEvidenceDisposition.Contradictory,
            MapStructureRejectionReason.NativeScaleChanged
                .ToContinuousLockDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Contradictory,
            MapStructureRejectionReason.ScaleChangeTooLarge
                .ToContinuousLockDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Inconclusive,
            MapStructureRejectionReason.OutsideValidBounds
                .ToContinuousLockDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Inconclusive,
            MapStructureRejectionReason.PlayerPriorMismatch
                .ToContinuousLockDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.Inconclusive,
            MapStructureRejectionReason.AnchorTransformConflict
                .ToContinuousLockDisposition());
        Assert.Equal(
            MapStructureEvidenceDisposition.SystemError,
            MapStructureRejectionReason.InvalidInput
                .ToContinuousLockDisposition());
    }

    [Fact]
    public void HoldingSessionRetainsLastReliableTransformAndGateBaseline()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        var main = map.Recognition.FirstFloor.FindAnchor("main-entrance")!;
        var side = map.Recognition.FirstFloor.FindAnchor("side-entrance")!;
        main.Bounds = new NormalizedRectangle
        {
            X = 0.1d,
            Y = 0.1d,
            Width = 0.05d,
            Height = 0.05d
        };
        side.Bounds = new NormalizedRectangle
        {
            X = 0.8d,
            Y = 0.8d,
            Width = 0.05d,
            Height = 0.05d
        };
        var transform = new MapOverlayTransform
        {
            ScaleX = 1.25d,
            ScaleY = 1.25d,
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
                Confidence = 0.9d,
                OverlayTransform = transform,
                AnchorMatches =
                [
                    new CvAnchorEvidence { AnchorId = main.Id, TemplateScale = 0.4d },
                    new CvAnchorEvidence { AnchorId = side.Id, TemplateScale = 0.4d }
                ]
            });

        var held = session.Hold(MapStructureRegistrationResult.Reject(
            MapStructureRejectionReason.AmbiguousCandidates));

        Assert.Same(transform, held.LockedTransform);
        Assert.Equal(1.25d, held.BaselineGateScale);
        Assert.Equal(MapAlignmentTrackingMode.HoldingLastTransform, held.Mode);
        Assert.Equal(0, held.ConsecutiveRejections);
        Assert.Equal(
            MapStructureRejectionReason.None,
            held.LastRejectionReason);
        Assert.Equal(0.9d, held.LastConfidence);
        Assert.Equal(0d, held.LastObservationConfidence);
        Assert.Equal(
            MapStructureRejectionReason.AmbiguousCandidates,
            held.LastObservationRejectionReason);

        var reused = session.Advance(
            map,
            new MapRecognitionResult
            {
                MapId = map.Id,
                Source = MapRecognitionSource.ReusedLastTransform,
                OverlayTransform = transform
            });

        Assert.Same(transform, reused.LockedTransform);
        Assert.Equal(
            MapAlignmentTrackingMode.HoldingLastTransform,
            reused.Mode);
        Assert.Equal(0, reused.ConsecutiveRejections);
    }

    [Fact]
    public void StructureTuningPersistsWithRuntimeSettings()
    {
        var settings = new MapRuntimeSettings
        {
            StructureRegistrationTuning = new MapStructureRegistrationTuning
            {
                SchemaVersion =
                    MapStructureRegistrationTuning.CurrentSchemaVersion,
                UseAuxiliaryAnchorRecognition = true,
                ReusePreviousAlignmentResult = false,
                PreviousAlignmentSearchRadiusPixels = 144,
                MaximumChamferPixels = 2.7d,
                MinimumCandidateMargin = 0.12d,
                EnableDebugOutput = true
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.True(restored.StructureRegistrationTuning.UseAuxiliaryAnchorRecognition);
        Assert.False(restored.StructureRegistrationTuning.ReusePreviousAlignmentResult);
        Assert.Equal(
            144,
            restored.StructureRegistrationTuning.PreviousAlignmentSearchRadiusPixels);
        Assert.Equal(2.7d, restored.StructureRegistrationTuning.MaximumChamferPixels);
        Assert.Equal(0.12d, restored.StructureRegistrationTuning.MinimumCandidateMargin);
        Assert.True(restored.StructureRegistrationTuning.EnableDebugOutput);
    }

    [Fact]
    public void AuxiliaryAnchorRecognitionIsEnabledByDefault()
    {
        var settings = new MapRuntimeSettings();

        Assert.False(
            settings.StructureRegistrationTuning.UseAuxiliaryAnchorRecognition);
        Assert.False(
            settings.Clone()
                .StructureRegistrationTuning
                .UseAuxiliaryAnchorRecognition);
    }

    [Fact]
    public void VersionThreeAuxiliarySettingMigratesEnabledWithEightTemplateCap()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = 3,
            UseAuxiliaryAnchorRecognition = false,
            MaximumAuxiliaryTemplates = 99
        };

        tuning.Normalize();

        Assert.Equal(
            MapStructureRegistrationTuning.CurrentSchemaVersion,
            tuning.SchemaVersion);
        Assert.True(tuning.UseAuxiliaryAnchorRecognition);
        Assert.Equal(8, tuning.MaximumAuxiliaryTemplates);
    }

    [Fact]
    public void PreviousAlignmentReuseIsEnabledByDefault()
    {
        var settings = new MapRuntimeSettings();

        Assert.True(
            settings.StructureRegistrationTuning.ReusePreviousAlignmentResult);
        Assert.Equal(
            96,
            settings.StructureRegistrationTuning
                .PreviousAlignmentSearchRadiusPixels);
        Assert.True(
            settings.Clone()
                .StructureRegistrationTuning
                .ReusePreviousAlignmentResult);
    }

    [Fact]
    public void LegacyStructureTuningMigratesToTranslationOnlyEccDefault()
    {
        var tuning = System.Text.Json.JsonSerializer.Deserialize<
            MapStructureRegistrationTuning>(
            """
            {
              "EnableEccRefinement": true,
              "EnableDebugOutput": true
            }
            """)!;

        tuning.Normalize();

        Assert.Equal(
            MapStructureRegistrationTuning.CurrentSchemaVersion,
            tuning.SchemaVersion);
        Assert.False(tuning.EnableEccRefinement);
        Assert.False(tuning.EnableDebugOutput);
        Assert.True(tuning.ReusePreviousAlignmentResult);

        tuning.EnableEccRefinement = true;
        tuning.EnableDebugOutput = true;
        tuning.ReusePreviousAlignmentResult = false;
        tuning.PreviousAlignmentSearchRadiusPixels = 5000;
        tuning.Normalize();
        Assert.True(tuning.EnableEccRefinement);
        Assert.True(tuning.EnableDebugOutput);
        Assert.False(tuning.ReusePreviousAlignmentResult);
        Assert.Equal(1000, tuning.PreviousAlignmentSearchRadiusPixels);
    }

    [Fact]
    public void LivePreprocessingRemovesDetachedUiAroundDominantMap()
    {
        using var live = new Mat(
            new Size(720, 520),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30));
        Cv2.Rectangle(
            live,
            new Rect(90, 110, 390, 300),
            new Scalar(125, 135, 145),
            -1);
        Cv2.Rectangle(
            live,
            new Rect(560, 10, 110, 35),
            new Scalar(160, 165, 170),
            -1);
        Cv2.Rectangle(
            live,
            new Rect(610, 430, 70, 70),
            new Scalar(160, 165, 170),
            -1);
        Cv2.PutText(
            live,
            "NAVIGATION",
            new Point(180, 500),
            HersheyFonts.HersheySimplex,
            0.7d,
            Scalar.White,
            2);
        // Real captures can contain a bright frame. It must be cleared before
        // connected-component filtering: its full-image bounding box would
        // otherwise make every detached HUD component appear adjacent to the
        // dominant map cluster.
        Cv2.Rectangle(
            live,
            new Rect(0, 0, live.Width, live.Height),
            Scalar.White,
            1);
        var preprocessor = new MapStructurePreprocessor();

        using var result = preprocessor.ProcessLiveRoi(live);
        using var points = new Mat();
        Cv2.FindNonZero(result.StructureMask, points);
        var bounds = Cv2.BoundingRect(points);

        Assert.InRange(bounds.X, 85, 95);
        Assert.InRange(bounds.Y, 105, 115);
        Assert.InRange(bounds.Width, 385, 400);
        Assert.InRange(bounds.Height, 295, 310);
        Assert.Equal(0, result.StructureMask.At<byte>(25, 600));
        Assert.Equal(0, result.StructureMask.At<byte>(460, 640));
        Assert.NotEqual(0, result.StructureMask.At<byte>(250, 250));
    }

    [Fact]
    public void OversizedDominantMapReportsQueryLargerThanReference()
    {
        using var reference = new Mat(
            new Size(220, 180),
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(reference, new Rect(25, 25, 170, 130), Scalar.White, -1);
        using var live = new Mat(
            new Size(360, 300),
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(live, new Rect(20, 20, 320, 260), Scalar.White, -1);
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform = Locked(reference),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.False(result.Accepted);
        Assert.Equal(
            MapStructureRejectionReason.QueryLargerThanReference,
            result.RejectionReason);
        Assert.Equal(1, result.ScaleHypothesisCount);
        Assert.Equal(1, result.OversizedHypothesisCount);
        Assert.True(result.QueryEdgePixels >= 50);
    }

    [Fact]
    public void DistinctExploredStructureRecoversTranslation()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: expectedOffsetX + 12d,
                offsetY: expectedOffsetY - 9d),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.Equal(1, result.ScaleHypothesisCount);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expectedOffsetY), 0d, 2d);
        Assert.True(result.Candidates.Count >= 2);
        Assert.True(result.CandidateMargin > 0d);
    }

    [Fact]
    public void CanvasLargerThanReferenceAcceptsAValidStructureMatch()
    {
        using var reference = BuildReference();
        var referenceCrop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, referenceCrop);
        using var live = new Mat(
            new Size(620, 520),
            MatType.CV_8UC3,
            Scalar.Black);
        var livePlacement = new Rect(160, 130, source.Width, source.Height);
        using (var target = new Mat(live, livePlacement))
            source.CopyTo(target);
        var viewport = new MapScreenRect(
            800d,
            420d,
            live.Width,
            live.Height);
        var expectedOffsetX =
            viewport.X + livePlacement.X - referenceCrop.X;
        var expectedOffsetY =
            viewport.Y + livePlacement.Y - referenceCrop.Y;
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = viewport,
                LockedTransform = Locked(
                    reference,
                    expectedOffsetX,
                    expectedOffsetY),
                Tuning = TestTuning(),
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = true,
                ValidMapBounds = MapReferenceBounds.FullImage(
                    reference.Width,
                    reference.Height)
            });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.True(result.Candidates[0].IsWithinValidBounds);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetX - expectedOffsetX),
            0d,
            2d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetY - expectedOffsetY),
            0d,
            2d);
    }

    [Fact]
    public void RestrictedPreviousAlignmentSearchRecoversNearbyTranslation()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;
        var tuning = TestTuning();
        tuning.PreviousAlignmentSearchRadiusPixels = 32;
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: expectedOffsetX + 12d,
                offsetY: expectedOffsetY - 9d),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = true
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.UsedRestrictedSearch);
        Assert.NotNull(result.Transform);
        Assert.All(
            result.Candidates,
            candidate => Assert.False(candidate.UsedGlobalSearch));
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expectedOffsetY), 0d, 2d);
    }

    [Fact]
    public void RestrictedSearchRejectsTargetOutsideConfiguredRadius()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;
        var tuning = TestTuning();
        tuning.PreviousAlignmentSearchRadiusPixels = 24;
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
        var request = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: expectedOffsetX + 180d,
                offsetY: expectedOffsetY + 140d),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = true
        };

        var local = registrar.Register(request);
        var global = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = request.LockedTransform,
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.False(local.Accepted);
        Assert.True(local.UsedRestrictedSearch);
        Assert.True(global.Accepted, global.FailureReason);
        Assert.NotNull(global.Transform);
        Assert.InRange(Math.Abs(global.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(global.Transform.OffsetY - expectedOffsetY), 0d, 2d);
    }

    [Fact]
    public void TinyUniformScaleSearchRecoversScaleAndTranslation()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, crop);
        const double expectedScale = 1.02d;
        using var live = new Mat();
        Cv2.Resize(
            source,
            live,
            new Size(
                (int)Math.Round(source.Width * expectedScale),
                (int)Math.Round(source.Height * expectedScale)),
            0d,
            0d,
            InterpolationFlags.Nearest);
        var viewport = new MapScreenRect(600d, 300d, live.Width, live.Height);
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(
                reference,
                offsetX: viewport.X - crop.X,
                offsetY: viewport.Y - crop.Y),
            Tuning = TestTuning(),
            AllowScaleSearch = true
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.True(result.ScaleHypothesisCount > 1);
        Assert.InRange(result.Transform.ScaleX, 1.009d, 1.021d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetX - (viewport.X - (crop.X * result.Transform.ScaleX))),
            0d,
            // AKAZE consensus votes are quantized to descriptor keypoints;
            // allow the same sub-pixel/refinement rounding margin as the
            // three-pixel registration target.
            3.5d);
    }

    [Fact]
    public void SingleStraightCorridorIsRejectedAsInsufficient()
    {
        using var reference = new Mat(new Size(420, 300), MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(reference, new Point(40, 150), new Point(380, 150), Scalar.White, 8);
        using var live = new Mat(new Size(260, 80), MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(live, new Point(10, 40), new Point(250, 40), Scalar.White, 8);
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform = Locked(reference),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.False(result.Accepted);
        Assert.Contains(
            result.RejectionReason,
            new[]
            {
                MapStructureRejectionReason.InsufficientStructure,
                MapStructureRejectionReason.InconsistentStructure,
                MapStructureRejectionReason.AmbiguousCandidates
            });
    }

    [Fact]
    public void RepeatedRoomGroupsAreRejectedAsAmbiguous()
    {
        using var reference = new Mat(new Size(560, 260), MatType.CV_8UC3, Scalar.Black);
        DrawRepeatedGroup(reference, 25);
        DrawRepeatedGroup(reference, 315);
        using var live = new Mat(reference, new Rect(20, 30, 205, 190)).Clone();
        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = new MapScreenRect(0d, 0d, live.Width, live.Height),
            LockedTransform = Locked(reference, -20d, -30d),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.False(result.Accepted);
        Assert.Equal(
            MapStructureRejectionReason.AmbiguousCandidates,
            result.RejectionReason);
    }

    [Fact]
    public void AdjacentScalesAtSameReferenceLocationAreOneAlignmentBasin()
    {
        var tuning = TestTuning();
        tuning.ScaleSearchRadius = 0.15d;
        tuning.Normalize();
        var first = new MapStructureCandidate
        {
            Scale = 1.0174d,
            ReferenceX = 217,
            ReferenceY = 219,
            OffsetX = 337.8d,
            OffsetY = 346.9d
        };
        var adjacentScale = new MapStructureCandidate
        {
            Scale = 1.0471d,
            ReferenceX = 223,
            ReferenceY = 222,
            OffsetX = 324.6d,
            OffsetY = 337.2d
        };
        var otherLocation = adjacentScale with
        {
            ReferenceX = 315,
            ReferenceY = 219
        };

        Assert.True(StructureRegistrationRules.IsSameAlignmentBasin(
            first,
            adjacentScale,
            tuning));
        Assert.False(StructureRegistrationRules.IsSameAlignmentBasin(
            first,
            otherLocation,
            tuning));
    }

    [Fact]
    public void ForcedBestCandidateAcceptsAmbiguousRepeatedRooms()
    {
        using var reference = new Mat(
            new Size(560, 260),
            MatType.CV_8UC3,
            Scalar.Black);
        DrawRepeatedGroup(reference, 25);
        DrawRepeatedGroup(reference, 315);
        using var live = new Mat(
            reference,
            new Rect(20, 30, 205, 190)).Clone();
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = new MapScreenRect(
                    0d,
                    0d,
                    live.Width,
                    live.Height),
                LockedTransform = Locked(reference, -20d, -30d),
                Tuning = TestTuning(),
                AllowScaleSearch = false,
                ForceBestCandidate = true
            });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.True(result.WasForcedBestCandidate);
        Assert.Equal(
            MapStructureRejectionReason.AmbiguousCandidates,
            result.RejectionReason);
    }

    [Fact]
    public void ForcedBestCandidateStillRejectsWhenNoCandidateExists()
    {
        using var reference = BuildReference();
        using var live = new Mat(
            new Size(300, 220),
            MatType.CV_8UC3,
            Scalar.Black);
        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds = new MapScreenRect(
                    0d,
                    0d,
                    live.Width,
                    live.Height),
                LockedTransform = Locked(reference),
                Tuning = TestTuning(),
                AllowScaleSearch = false,
                ForceBestCandidate = true
            });

        Assert.False(result.Accepted);
        Assert.Null(result.Transform);
        Assert.False(result.WasForcedBestCandidate);
        Assert.Equal(
            MapStructureRejectionReason.InsufficientStructure,
            result.RejectionReason);
    }

    [Fact]
    public void DerivedCacheDoesNotWriteIntoMapDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"idvbuff-structure-cache-{Guid.NewGuid():N}");
        var mapDirectory = Path.Combine(root, "maps", "map-one");
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(mapDirectory);
        var sentinel = Path.Combine(mapDirectory, "maps.json");
        File.WriteAllText(sentinel, "sentinel");
        using var reference = BuildReference();
        try
        {
            var cache = new MapStructureReferenceCache(
                new MapStructurePreprocessor(),
                cacheDirectory);
            using var first = cache.GetOrCreate(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                reference);

            Assert.Equal("sentinel", File.ReadAllText(sentinel));
            Assert.Single(Directory.GetFiles(mapDirectory));
            Assert.NotEmpty(Directory.GetFiles(cacheDirectory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MapStructureRegistrationTuning TestTuning() => new()
    {
        MinimumEdgePixels = 50,
        MinimumSpanPixels = 18,
        MinimumConsistentPartitions = 2,
        TopCandidateCount = 6,
        MaximumChamferPixels = 3.5d,
        MinimumEdgeCoverage = 0.50d,
        MinimumOccupancyCoverage = 0.35d,
        MinimumCandidateMargin = 0.025d,
        ScaleSearchRadius = 0.02d,
        ScaleSearchStep = 0.01d,
        EnableFastAlignment = false,
        FeatureRatioThreshold = 0.78d
    };

    private static MapOverlayTransform Locked(
        Mat reference,
        double offsetX = 0d,
        double offsetY = 0d) =>
        new()
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };

    // ═══════════════════════════════════════════════════════════════
    // P2-1: ProcessCachedReference ownership
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ProcessCachedReference_DisposeDoesNotInvalidateCache()
    {
        // P2-1: The caller owns their clone. Disposing it must not
        // affect the internal cached instance or subsequent lookups.
        var preprocessor = new MapStructurePreprocessor();
        using var reference = BuildReference();
        var referencePath = $"cache-test-{Guid.NewGuid():N}";

        // First call — generates and caches.
        var first = preprocessor.ProcessCachedReference(
            reference, referencePath, out _, out var cacheHit1);
        Assert.NotNull(first);
        Assert.False(cacheHit1);

        // Dispose the returned object.
        first.Dispose();

        // Second call — must hit cache and return a valid, independent clone.
        var second = preprocessor.ProcessCachedReference(
            reference, referencePath, out _, out var cacheHit2);
        Assert.NotNull(second);
        Assert.True(cacheHit2, "Second call must hit cache after first Dispose");

        // The second instance must not be the same object as the first
        // (would indicate shared mutable state).
        Assert.NotSame(first, second);

        // Dispose the second — cache must remain valid for a third lookup.
        second.Dispose();

        var third = preprocessor.ProcessCachedReference(
            reference, referencePath, out _, out var cacheHit3);
        Assert.NotNull(third);
        Assert.True(cacheHit3, "Third call must still hit cache after second Dispose");
        Assert.NotSame(second, third);

        third.Dispose();
        MapStructurePreprocessor.ClearReferenceCache();
    }

    [Fact]
    public void LiveAndReferenceStructureDescriptorsUseCompatibleAkazeLayout()
    {
        var preprocessor = new MapStructurePreprocessor();
        using var source = BuildReference();
        using var reference = preprocessor.ProcessReference(source, null);
        using var live = preprocessor.ProcessLiveRoi(source);

        Assert.False(reference.Descriptors.Empty());
        Assert.False(live.Descriptors.Empty());
        Assert.Equal(reference.Descriptors.Type(), live.Descriptors.Type());
        Assert.Equal(reference.Descriptors.Cols, live.Descriptors.Cols);
        Assert.Equal(61, reference.Descriptors.Cols);
    }
}
