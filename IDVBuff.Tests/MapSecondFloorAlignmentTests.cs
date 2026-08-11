using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapSecondFloorAlignmentTests
{
    [Fact]
    public void CrossFloorSeedAdjustsScaleButDiscardsTranslation()
    {
        var source = new MapOverlayTransform
        {
            ScaleX = 1.2d,
            ScaleY = 1.2d,
            OffsetX = 400d,
            OffsetY = 200d,
            ReferenceCenterX = 500d,
            ReferenceCenterY = 400d,
            ScreenCenterX = 1000d,
            ScreenCenterY = 680d,
            ReferenceWidth = 1000,
            ReferenceHeight = 800,
            OrientationDegrees = 0,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = 3d
        };
        var sourceFloor = new FloorRecognitionProfile
        {
            FloorKey = "1f",
            RecognitionPixelWidth = 1000,
            RecognitionPixelHeight = 800,
            OrientationDegrees = 0
        };
        var targetFloor = new FloorRecognitionProfile
        {
            FloorKey = "2f",
            RecognitionPixelWidth = 500,
            RecognitionPixelHeight = 400,
            OrientationDegrees = 0
        };

        var result = MapFloorScaleSeedRules.RenormalizeTransformToFloor(
            source,
            sourceFloor,
            targetFloor);

        // scale = 1.2 × ((1000/500) + (800/400)) / 2 = 2.4.
        // Translation is deliberately discarded across floors.
        Assert.Equal(2.4d, result.ScaleX, 8);
        Assert.Equal(2.4d, result.ScaleY, 8);
        Assert.Equal(500, result.ReferenceWidth);
        Assert.Equal(400, result.ReferenceHeight);
        Assert.Equal(250d, result.ReferenceCenterX, 8);
        Assert.Equal(200d, result.ReferenceCenterY, 8);
        Assert.Equal(0, result.OrientationDegrees);
        Assert.Equal(0d, result.OffsetX, 8);
        Assert.Equal(0d, result.OffsetY, 8);
    }

    [Fact]
    public void CrossFloorSeedIgnoresDimensionRatioWhenFloorExtentsDiffer()
    {
        var source = new MapOverlayTransform
        {
            ScaleX = 0.9878022620589495d,
            ScaleY = 0.9878022620589495d,
            ReferenceWidth = 1129,
            ReferenceHeight = 1196,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        var sourceFloor = new FloorRecognitionProfile
        {
            RecognitionPixelWidth = 1129,
            RecognitionPixelHeight = 1196
        };
        var targetFloor = new FloorRecognitionProfile
        {
            RecognitionPixelWidth = 1198,
            RecognitionPixelHeight = 658
        };

        var ratio = MapFloorScaleSeedRules.ResolveReferenceScaleRatio(
            sourceFloor,
            targetFloor,
            out var usedDimensionRatio);
        var result = MapFloorScaleSeedRules.RenormalizeTransformToFloor(
            source,
            sourceFloor,
            targetFloor);

        // Different aspect ratios represent different world extents rather
        // than a reliable pixel-density ratio.
        Assert.False(usedDimensionRatio);
        Assert.Equal(1d, ratio, 6);
        Assert.Equal(source.ScaleX, result.ScaleX, 6);
        Assert.Equal(source.ScaleY, result.ScaleY, 6);
    }

    [Fact]
    public void CrossFloorSeed_DoesNotAverageCurrentPrimaryAndSecondFloorExtents()
    {
        var sourceFloor = new FloorRecognitionProfile
        {
            RecognitionPixelWidth = 1199,
            RecognitionPixelHeight = 970
        };
        var targetFloor = new FloorRecognitionProfile
        {
            RecognitionPixelWidth = 1195,
            RecognitionPixelHeight = 852
        };

        var ratio = MapFloorScaleSeedRules.ResolveReferenceScaleRatio(
            sourceFloor,
            targetFloor,
            out var usedDimensionRatio);

        Assert.False(usedDimensionRatio);
        Assert.Equal(1d, ratio, 8);
    }

    [Fact]
    public void SecondFloorStructureRecognitionCarriesStructureEvidence()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        map.Recognition.EnsureStandardAnchors();
        map.Recognition.SecondFloor.OrientationDegrees = 0;
        var transform = new MapOverlayTransform
        {
            ScaleX = 1.2d,
            ScaleY = 1.2d,
            ReferenceWidth = 1000,
            ReferenceHeight = 800,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        var structure = new MapStructureRegistrationResult
        {
            Accepted = true,
            Transform = transform,
            Confidence = 0.88d,
            BestScore = 12d,
            SecondScore = 4d,
            CandidateMargin = 0.2d,
            RejectionReason = MapStructureRejectionReason.None
        };

        var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(
            map,
            "2f",
            "C:\\fake\\floor-2.png",
            transform,
            structure);

        Assert.Same(map, recognition.Map);
        Assert.Equal("C:\\fake\\floor-2.png", recognition.FloorImagePath);
        Assert.Equal("2f", recognition.Result.Floor);
        Assert.Equal(
            MapRecognitionSource.StructureMatching,
            recognition.Result.Source);
        Assert.Equal(0.88d, recognition.Result.Confidence);
        Assert.Equal(0.88d, recognition.Result.LocalizationConfidence);
        Assert.Equal(
            MapAlignmentEvidenceKind.Structure,
            recognition.Result.EvidenceKind);
        Assert.Empty(recognition.Result.AnchorMatches);
        Assert.Same(transform, recognition.Result.OverlayTransform);
    }

    [Fact]
    public void IdentityPriorDoesNotInflateWeakSecondFloorLocalization()
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
            ReferenceWidth = 1000,
            ReferenceHeight = 800,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        var structure = new MapStructureRegistrationResult
        {
            Accepted = true,
            Transform = transform,
            Confidence = 0.52d,
            CandidateMargin = 0.05d
        };

        var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(
            map,
            "2f",
            "C:\\fake\\floor-2.png",
            transform,
            structure,
            identityPriorConfidence: 0.845d);

        Assert.Equal(0.845d, recognition.Result.IdentityConfidence);
        Assert.Equal(0.52d, recognition.Result.LocalizationConfidence);
        Assert.Equal(0.52d, recognition.Result.Confidence);
    }

    [Fact]
    public void RecognitionAttemptPublishesSplitConfidenceDiagnostics()
    {
        var diagnostics = new MapScanDiagnostics();
        var recognition = new RuntimeMapRecognition
        {
            Result = new MapRecognitionResult
            {
                Confidence = 0.61d,
                IdentityConfidence = 0.88d,
                LocalizationConfidence = 0.61d
            }
        };

        _ = new MapRecognitionAttempt
        {
            Recognition = recognition,
            Diagnostics = diagnostics
        };

        Assert.Equal(0.88d, diagnostics.IdentityConfidence);
        Assert.Equal(0.61d, diagnostics.LocalizationConfidence);
    }

    [Fact]
    public async Task AlignSecondFloorRejectsInvalidLockedScaleBeforeReadingReference()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.SecondFloor.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var floorOne = Path.Combine(root, "source-1.png");
            var floorTwo = Path.Combine(root, "source-2.png");
            using (var source = new Mat(
                new Size(200, 100),
                MatType.CV_8UC3,
                Scalar.All(255)))
            {
                Cv2.Rectangle(
                    source,
                    new Rect(40, 25, 50, 30),
                    Scalar.All(0),
                    -1);
                Assert.True(Cv2.ImWrite(floorOne, source));
                Assert.True(Cv2.ImWrite(floorTwo, source));
            }

            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.RecognitionRegion =
                new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.8d, Height = 0.8d };
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1d, Y = 0.2d, Width = 0.1d, Height = 0.1d };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.7d, Y = 0.6d, Width = 0.1d, Height = 0.1d };

            var repository = new MapRepository(Path.Combine(root, "maps"));
            var saved = await repository.SaveAsync(new MapDraft
            {
                FloorOnePath = floorOne,
                FloorTwoPath = floorTwo,
                Recognition = recognition
            });
            using var service = new MapCvRecognitionService(repository);
            await service.RefreshCacheAsync();
            using var frame = new CapturedGameFrame(
                Mat.Zeros(64, 64, MatType.CV_8UC3),
                new MapScreenRect(0d, 0d, 1920d, 1080d),
                new MapScreenRect(0d, 0d, 1920d, 1080d),
                IntPtr.Zero);
            var baseline = new MapOverlayTransform
            {
                ScaleX = 0.01d,
                ScaleY = 0.01d,
                OffsetX = 0d,
                OffsetY = 0d,
                ReferenceWidth = 500,
                ReferenceHeight = 400,
                AlignmentMode = MapOverlayAlignmentMode.Uniform
            };

            var attempt = service.AlignFloorWithoutGates(
                frame,
                saved.Id,
                "2f",
                baseline,
                MapOverlayAlignmentMode.Uniform,
                new MapRecognitionTuning());

            Assert.Null(attempt.Recognition);
            Assert.Equal(
                MapAlignmentTrackingMode.NeedsGatePair,
                attempt.Diagnostics.TrackingMode);
            Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
            Assert.Equal(0d, attempt.Diagnostics.GateDetectionMilliseconds);
            Assert.Contains("primary scale seed", attempt.FailureReason);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
