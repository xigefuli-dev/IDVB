using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructureRegistrarTests
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

        Assert.True(
            settings.StructureRegistrationTuning.UseAuxiliaryAnchorRecognition);
        Assert.True(
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
        Assert.True(tuning.EnableEccRefinement);
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
            3d);
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
        ScaleSearchStep = 0.01d
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
    public void ProcessCachedReference_NoPathDoesNotCacheAndReturnsDirectly()
    {
        // P2-1: When referencePath is null, no caching occurs and the
        // caller receives the result directly (no Clone needed).
        var preprocessor = new MapStructurePreprocessor();
        using var reference = BuildReference();

        var result = preprocessor.ProcessCachedReference(
            reference, null, out _, out var cacheHit);

        Assert.NotNull(result);
        Assert.False(cacheHit);
        // Clean up — the caller owns this instance.
        result.Dispose();
    }

    private static Mat BuildReference()
    {
        var image = new Mat(new Size(480, 360), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(35, 35, 90, 70), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(185, 25, 120, 55), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(350, 50, 65, 115), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(70, 175, 75, 120), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(210, 145, 95, 105), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(345, 235, 95, 75), Scalar.White, -1);
        Cv2.Line(image, new Point(125, 70), new Point(185, 52), Scalar.White, 18);
        Cv2.Line(image, new Point(275, 80), new Point(260, 145), Scalar.White, 16);
        Cv2.Line(image, new Point(145, 225), new Point(210, 200), Scalar.White, 14);
        Cv2.Line(image, new Point(305, 210), new Point(345, 270), Scalar.White, 12);
        Cv2.Circle(image, new Point(255, 200), 22, Scalar.Black, -1);
        return image;
    }

    private static void DrawRepeatedGroup(Mat image, int x)
    {
        Cv2.Rectangle(image, new Rect(x, 45, 70, 55), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(x + 105, 35, 65, 80), Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(x + 35, 135, 95, 55), Scalar.White, -1);
        Cv2.Line(
            image,
            new Point(x + 65, 82),
            new Point(x + 115, 75),
            Scalar.White,
            14);
        Cv2.Line(
            image,
            new Point(x + 85, 110),
            new Point(x + 85, 145),
            Scalar.White,
            12);
        Cv2.Circle(image, new Point(x + 82, 163), 13, Scalar.Black, -1);
    }

    // ═══════════════════════════════════════════════════════════════
    // 快速粗搜索单元测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FastAlignment_DefaultDisabled()
    {
        var tuning = new MapStructureRegistrationTuning();
        tuning.Normalize();

        Assert.False(tuning.EnableFastAlignment);
        Assert.True(tuning.FastFallbackToLegacy);
        Assert.False(tuning.FastAlignmentShadowMode);
        Assert.Equal(4, tuning.FastCoarseDownsampleFactor);
        Assert.Equal(5, tuning.FastCoarseTopK);
    }

    [Fact]
    public void FastAlignment_TuningRoundTrips()
    {
        var original = new MapStructureRegistrationTuning
        {
            EnableFastAlignment = true,
            FastFallbackToLegacy = false,
            FastAlignmentShadowMode = true,
            FastCoarseDownsampleFactor = 8,
            FastCoarseTopK = 10,
            FastCoarseNmsRadius = 24,
            FastCoarseMaxDimension = 200
        };

        var clone = original.Clone();
        clone.Normalize();

        Assert.True(clone.EnableFastAlignment);
        Assert.False(clone.FastFallbackToLegacy);
        Assert.True(clone.FastAlignmentShadowMode);
        Assert.Equal(8, clone.FastCoarseDownsampleFactor);
        Assert.Equal(10, clone.FastCoarseTopK);
        Assert.Equal(24, clone.FastCoarseNmsRadius);
        Assert.Equal(200, clone.FastCoarseMaxDimension);
    }

    [Fact]
    public void FastAlignment_TuningClamped()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            FastCoarseDownsampleFactor = 1,  // below min
            FastCoarseTopK = 1,              // below min
            FastCoarseNmsRadius = 1,         // below min
            FastCoarseMaxDimension = 10      // below min
        };
        tuning.Normalize();

        Assert.Equal(2, tuning.FastCoarseDownsampleFactor);
        Assert.Equal(3, tuning.FastCoarseTopK);
        Assert.Equal(4, tuning.FastCoarseNmsRadius);
        Assert.Equal(40, tuning.FastCoarseMaxDimension);
    }

    [Fact]
    public void FastCoarseAlign_FindsCorrectTranslation_WithStructuredQuery()
    {
        // 使用与 DistinctExploredStructureRecoversTranslation 相同场景
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(800d, 420d, live.Width, live.Height);
        var expectedOffsetX = viewport.X - crop.X;
        var expectedOffsetY = viewport.Y - crop.Y;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var tuning = TestFastTuning();
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
            AllowScaleSearch = false
        });

        // 快速路径可能因粗搜索精度不足而回退到 Legacy，
        // 但无论哪种路径，结果都应该正确
        Assert.True(result.Accepted, result.FailureReason);
        Assert.InRange(
            Math.Abs(result.Transform!.OffsetX - expectedOffsetX),
            0d,
            4d);
        Assert.InRange(
            Math.Abs(result.Transform.OffsetY - expectedOffsetY),
            0d,
            4d);
        // 如果快速路径成功，应有候选计数
        if (result.UsedFastStrategy)
        {
            Assert.True(result.FastCoarseCandidateCount > 0);
        }
    }

    [Fact]
    public void FastCoarseAlign_CandidatesRankedByCompositeCost()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var tuning = TestFastTuning();
        // 增大候选数以增加快速路径成功率
        tuning.FastCoarseTopK = 10;
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Candidates.Count >= 1,
            $"Expected at least 1 candidate, got {result.Candidates.Count}");

        for (var i = 1; i < result.Candidates.Count; i++)
        {
            Assert.True(
                result.Candidates[i - 1].CompositeCost
                    <= result.Candidates[i].CompositeCost + 0.001d,
                $"Candidate[{i - 1}] cost {result.Candidates[i - 1].CompositeCost:F3} "
                + $"should be ≤ Candidate[{i}] cost {result.Candidates[i].CompositeCost:F3}");
        }
    }

    [Fact]
    public void FastCoarseAlign_FallbackToLegacy_WhenRejected()
    {
        // 使用一个非常小的 query 确保快速路径因结构不足而拒绝
        // FastFallbackToLegacy=true 时会回退到 Legacy
        using var reference = BuildReference();
        using var live = new Mat(
            new Size(30, 25),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30));  // 低对比度、少结构
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = true;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // 无论快速路径还是 Legacy 都不应该接受这种 query
        Assert.False(result.Accepted);
        // 回退模式：UsedFastStrategy 为 Legacy 的结果，因此应为 false
    }

    [Fact]
    public void FastCoarseAlign_NoFallback_ReturnsRejectionWithoutCrashing()
    {
        using var reference = BuildReference();
        // 使用极小的 query 确保因结构不足而被快速路径直接拒绝
        using var live = new Mat(
            new Size(20, 15),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30));
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = false;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // 无回退时直接返回快速路径的拒绝结果 — 不应崩溃
        Assert.False(result.Accepted);
        // 注意：用于输入结构不足的早期拒绝不会设置 UsedFastStrategy，
        // 因为它在候选收集之前就退出了
    }

    [Fact]
    public void ShadowMode_ReturnsLegacyResult()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.FastAlignmentShadowMode = true;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // Shadow Mode 下 Legacy 应该是最终的返回结果，
        // UsedFastStrategy 应为 false
        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy);
    }

    [Fact]
    public void ShadowMode_WithFastEnabled_StillReturnsLegacyNotFast()
    {
        // P0-3: When both FastAlignmentShadowMode and EnableFastAlignment
        // are true, the result MUST come from Legacy, not Fast.
        // This verifies Shadow takes priority over production Fast.
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;       // production Fast enabled
        tuning.FastFallbackToLegacy = false;     // production Fast would NOT fallback
        tuning.FastAlignmentShadowMode = true;   // but Shadow overrides

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // Even though EnableFastAlignment=true and FastFallbackToLegacy=false
        // (which would force a Fast-only return in production mode),
        // Shadow mode ensures Legacy is returned.
        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy,
            "Shadow mode must return Legacy (UsedFastStrategy=false), not Fast");
    }

    [Fact]
    public void ProductionFastNoFallback_ReturnsFastFailureWhenRejected()
    {
        // P0-3 matrix: EnableFastAlignment=true, FastFallbackToLegacy=false,
        // Shadow=false, TrackingMode=false. Fast fails → return Fast failure.
        using var reference = BuildReference();
        using var live = new Mat(
            new Size(20, 15),
            MatType.CV_8UC3,
            new Scalar(20, 25, 30)); // tiny, low-structure
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = false;
        tuning.FastAlignmentShadowMode = false;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        // Must return Fast's failure, not fall through to Legacy.
        Assert.False(result.Accepted);
    }

    [Fact]
    public void ProductionLegacyOnly_NoFastExecution()
    {
        // P0-3 matrix: EnableFastAlignment=false, Shadow=false.
        // Only Legacy should run.
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);

        var tuning = TestFastTuning();
        tuning.EnableFastAlignment = false;
        tuning.FastAlignmentShadowMode = false;

        var registrar = new MapStructureRegistrar(
            new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy,
            "Pure Legacy mode should not set UsedFastStrategy");
    }

    [Fact]
    public void LegacySearch_ReportsSubstageTimings()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        var tuning = TestTuning();
        tuning.EnableFastAlignment = false;
        tuning.EnableVisibleAwareInjection = false;
        tuning.EnableVisibleAwareShadow = false;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy);
        Assert.True(result.SearchMilliseconds > 0d);
        Assert.True(result.DistanceMapMilliseconds >= 0d);
        Assert.True(result.QueryConstructionMilliseconds >= 0d);
        Assert.True(result.HistoryCandidateMilliseconds >= 0d);
        Assert.True(result.FeatureVotingMilliseconds >= 0d);
        Assert.True(result.PyramidSearchMilliseconds >= 0d);
        Assert.True(result.LocalTemplateSearchMilliseconds >= 0d);
        Assert.True(result.GlobalTemplateSearchMilliseconds >= 0d);
        Assert.True(result.CandidateRankingMilliseconds >= 0d);
    }

    [Fact]
    public void VisibleAwareEarlyExit_IsEnabledWhenMigratingOlderTuning()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = 5,
            EnableVisibleAwareEarlyExit = false,
            VisibleAwareEarlyTerminationMaxCompositeCost = 0d
        };

        tuning.Normalize();

        Assert.Equal(MapStructureRegistrationTuning.CurrentSchemaVersion, tuning.SchemaVersion);
        Assert.True(tuning.EnableVisibleAwareEarlyExit);
        Assert.Equal(0.55d, tuning.VisibleAwareEarlyTerminationMaxCompositeCost, 8);
    }

    // ═══════════════════════════════════════════════════════════════
    // P0-2: Visible-aware 正确性测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void VisibleAware_NoCrashWithNonZeroQueryBounds()
    {
        // P0-2A: When query.Bounds is non-zero and smaller than the full
        // query image, BitwiseAnd must not throw due to mismatched Mat sizes.
        // The live image has content at an offset, so query.Bounds will have
        // non-zero X/Y after bounding box computation.
        using var reference = BuildReference();
        // Create a live image that's a portion of reference, placed within
        // a larger black canvas so the dominant structure cluster is not at (0,0).
        var referenceCrop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, referenceCrop);
        using var live = new Mat(
            new Size(400, 340),
            MatType.CV_8UC3,
            Scalar.Black);
        var livePlacement = new Rect(50, 52, source.Width, source.Height);
        using (var target = new Mat(live, livePlacement))
            source.CopyTo(target);

        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        var expectedOffsetX =
            viewport.X + livePlacement.X - referenceCrop.X;
        var expectedOffsetY =
            viewport.Y + livePlacement.Y - referenceCrop.Y;

        var tuning = TestTuning();
        tuning.EnableVisibleMask = true;
        tuning.EnableVisibleAwareShadow = true;
        tuning.EnableVisibleAwareInjection = true;
        // Lower thresholds so synthetic data passes.
        tuning.VisibleAwareMinimumVisibleStructurePixels = 10;
        tuning.VisibleAwareMinimumVisibleFraction = 0.01d;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        // This MUST NOT throw an OpenCV exception.
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = false
        });

        Assert.True(result.Accepted, result.FailureReason);
    }

    [Fact]
    public void VisibleAware_CandidatePositionNotDoublyOffset()
    {
        // P0-2B: The visible-aware path must not add query.Bounds.X/Y
        // twice to the MatchTemplate position. Verify the resulting
        // transform offset matches the expected (correct) value.
        using var reference = BuildReference();
        var referenceCrop = new Rect(82, 58, 300, 236);
        using var source = new Mat(reference, referenceCrop);
        // Place the source within a larger canvas at a non-zero position
        // so query.Bounds is non-zero and smaller than the full image.
        using var live = new Mat(
            new Size(400, 340),
            MatType.CV_8UC3,
            Scalar.Black);
        var livePlacement = new Rect(50, 52, source.Width, source.Height);
        using (var target = new Mat(live, livePlacement))
            source.CopyTo(target);

        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        // Correct offset: the live content at (50,52) came from reference
        // at (82,58). Scale=1 so offset = viewport + livePlacement - referenceCrop.
        var expectedOffsetX =
            viewport.X + livePlacement.X - referenceCrop.X;
        var expectedOffsetY =
            viewport.Y + livePlacement.Y - referenceCrop.Y;
        // If Bounds offset were doubled, the result would be off by
        // approximately query.Bounds.X/Y (which will be ~50px each).

        var tuning = TestTuning();
        tuning.EnableVisibleMask = true;
        tuning.EnableVisibleAwareShadow = true;
        tuning.EnableVisibleAwareInjection = true;
        tuning.VisibleAwareMinimumVisibleStructurePixels = 10;
        tuning.VisibleAwareMinimumVisibleFraction = 0.01d;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());

        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference, expectedOffsetX + 5d, expectedOffsetY - 5d),
            Tuning = tuning,
            AllowScaleSearch = false,
            RestrictSearchToLockedTransform = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);

        // The offset must NOT be off by ~query.Bounds.X or ~query.Bounds.Y
        // which would indicate double-offset regression.
        var offsetErrorX = Math.Abs(result.Transform.OffsetX - expectedOffsetX);
        var offsetErrorY = Math.Abs(result.Transform.OffsetY - expectedOffsetY);
        Assert.True(offsetErrorX < 20d,
            $"OffsetX error {offsetErrorX:F1}px — double-offset bug would be >40px");
        Assert.True(offsetErrorY < 20d,
            $"OffsetY error {offsetErrorY:F1}px — double-offset bug would be >40px");

        // Extra guard: a double-offset would place the result far from truth.
        // query.Bounds will be at least ~50px in each axis, so a double
        // addition would create errors >= 40px.
        Assert.True(offsetErrorX < 40d,
            $"OffsetX error {offsetErrorX:F1}px >= 40px suggests double-offset regression");
        Assert.True(offsetErrorY < 40d,
            $"OffsetY error {offsetErrorY:F1}px >= 40px suggests double-offset regression");
    }

    [Fact]
    public void VisibleAware_DisabledByDefaultDoesNotInterfere()
    {
        // Sanity check: with visible-aware off (default), the standard
        // registration path still works correctly.
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
            LockedTransform = Locked(reference, offsetX: expectedOffsetX + 12d, offsetY: expectedOffsetY - 9d),
            Tuning = TestTuning(),
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.NotNull(result.Transform);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expectedOffsetX), 0d, 2d);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expectedOffsetY), 0d, 2d);
    }

    private static MapStructureRegistrationTuning TestFastTuning() => new()
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
        EnableFastAlignment = true
    };
}
