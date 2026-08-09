using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapMultiFloorAlignmentTests
{
    [Fact]
    public void PrimaryFloorUsesStableSortOrderInsteadOfLiteralKey()
    {
        var map = new MapRecord
        {
            Floors =
            [
                new FloorDefinition { Key = "1f", DisplayName = "Old 1F", SortOrder = 3 },
                new FloorDefinition { Key = "roof-b", DisplayName = "Roof B", SortOrder = 1 },
                new FloorDefinition { Key = "roof-a", DisplayName = "Roof A", SortOrder = 1 }
            ]
        };

        Assert.Equal("roof-a", MapFloorRules.GetPrimaryFloorKey(map));
        Assert.True(MapFloorRules.UsesDoubleGateAlignment(map, "roof-a"));
        Assert.False(MapFloorRules.UsesDoubleGateAlignment(map, "roof-b"));
        Assert.False(MapFloorRules.UsesDoubleGateAlignment(map, "1f"));
        Assert.Equal("roof-b", MapFloorRules.GetFloorKeyAtPosition(map, 2));
        Assert.Equal(3, MapFloorRules.GetFloorPosition(map, "1f"));
    }

    [Fact]
    public void NextFloorKeyCyclesThroughAllUserDefinedFloors()
    {
        var map = new MapRecord
        {
            Floors =
            [
                new FloorDefinition { Key = "main", SortOrder = 1 },
                new FloorDefinition { Key = "upper", SortOrder = 2 },
                new FloorDefinition { Key = "basement", SortOrder = 3 }
            ]
        };

        Assert.Equal("upper", MapFloorRules.GetNextFloorKey(map, "main"));
        Assert.Equal("basement", MapFloorRules.GetNextFloorKey(map, "upper"));
        Assert.Equal("main", MapFloorRules.GetNextFloorKey(map, "basement"));
        Assert.Equal("main", MapFloorRules.GetNextFloorKey(map, "1f"));
    }

    [Fact]
    public void FloorScaleSamplesRemainIsolatedAndUseMedian()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var mapId = Guid.NewGuid();
        var floorTwo = Calibration("upper", mapId, updatedAt);
        var floorThree = Calibration("basement", mapId, updatedAt);

        Assert.True(floorTwo.TryAddTrustedSample(1.10d, 0.90d, updatedAt, out _));
        Assert.True(floorTwo.TryAddTrustedSample(1.12d, 0.91d, updatedAt, out _));
        Assert.True(floorTwo.TryAddTrustedSample(1.08d, 0.92d, updatedAt, out _));
        Assert.True(floorThree.TryAddTrustedSample(0.80d, 0.93d, updatedAt, out _));

        Assert.Equal(1.10d, floorTwo.MedianRatio, 8);
        Assert.Equal(0.80d, floorThree.MedianRatio, 8);
        Assert.NotEqual(floorTwo.MedianRatio, floorThree.MedianRatio);
    }

    [Fact]
    public void FloorScaleOutlierDoesNotPolluteTrustedSamples()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var calibration = Calibration("upper", Guid.NewGuid(), updatedAt);
        foreach (var ratio in new[] { 1.00d, 1.01d, 0.99d })
            Assert.True(calibration.TryAddTrustedSample(ratio, 0.9d, updatedAt, out _));

        Assert.False(calibration.TryAddTrustedSample(1.30d, 0.95d, updatedAt, out var reason));
        Assert.Contains("ratio-outlier", reason);
        Assert.Equal(3, calibration.RecentTrustedRatios.Count);
        Assert.Equal(1.00d, calibration.MedianRatio, 8);
    }

    [Fact]
    public void MapUpdateInvalidatesFloorRatio()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var mapId = Guid.NewGuid();
        var calibration = Calibration("upper", mapId, updatedAt);
        Assert.True(calibration.TryAddTrustedSample(1.1d, 0.9d, updatedAt, out _));

        Assert.True(calibration.Matches(mapId, updatedAt, "primary", "upper"));
        Assert.False(calibration.Matches(
            mapId,
            updatedAt.AddMilliseconds(1),
            "primary",
            "upper"));
        Assert.False(calibration.Matches(mapId, updatedAt, "primary", "basement"));
    }

    [Theory]
    [InlineData(true, 0.04d, 0.15d)]
    [InlineData(false, 0.15d, 0.30d)]
    public void FloorSearchPolicyUsesRequiredTwoStages(
        bool calibrated,
        double expectedInitial,
        double expectedExpanded)
    {
        var radii = MapFloorScaleSearchPolicy.GetRadii(calibrated);

        Assert.Equal(expectedInitial, radii.InitialRadius, 8);
        Assert.Equal(expectedExpanded, radii.ExpandedRadius, 8);
    }

    [Theory]
    [InlineData(0.15d)]
    [InlineData(0.30d)]
    public void RecoverySearchRadiusSurvivesTuningNormalization(double radius)
    {
        var tuning = new MapStructureRegistrationTuning
        {
            ScaleSearchRadius = radius
        };

        tuning.Normalize();

        Assert.Equal(radius, tuning.ScaleSearchRadius, 8);
    }

    [Fact]
    public void ConfidenceBreakdownCalculatesSeparatedScores()
    {
        var tuning = new MapStructureRegistrationTuning();
        tuning.Normalize();
        var candidate = new MapStructureCandidate
        {
            ChamferPixels = tuning.MaximumChamferPixels * 0.25d,
            EdgeCoverage = 0.80d,
            OccupancyCoverage = 0.70d,
            ConsistentPartitions = 3,
            FeatureInlierCount = 12,
            FeatureConsensus = 0.75d,
            EccConverged = true,
            EccCorrelation = 0.85d,
            IsWithinValidBounds = true,
            PriorAgreement = 0.90d
        };

        var breakdown = MapStructureConfidenceCalculator.Calculate(
            candidate,
            0.40d,
            tuning);
        Assert.Equal(0.77d, breakdown.GeometricFitQuality, 12);
        Assert.Equal(0.7525d, breakdown.EvidenceConfidence, 12);
        Assert.Equal(0.7525d, breakdown.GeometricLockConfidence, 12);
        Assert.Equal(0.7375d, breakdown.LockConfidence, 12);
        Assert.Equal(breakdown.LockConfidence, breakdown.FinalScore, 12);
        Assert.Equal(0.70d, breakdown.EffectiveWeight, 12);
        Assert.Null(breakdown.LowEvidenceReason);
    }

    [Fact]
    public void LowCoverageIsDiagnosticButRetainsCalibratedRuntimeScore()
    {
        var tuning = new MapStructureRegistrationTuning();
        tuning.Normalize();
        var candidate = new MapStructureCandidate
        {
            ChamferPixels = 0d,
            EdgeCoverage = 0.90d,
            OccupancyCoverage = 0.10d,
            ConsistentPartitions = 1,
            IsWithinValidBounds = true,
            PriorAgreement = 1d
        };

        var breakdown = MapStructureConfidenceCalculator.Calculate(
            candidate,
            0.40d,
            tuning);

        Assert.Equal(0.91d, breakdown.GeometricLockConfidence, 12);
        Assert.Equal(0.6875d, breakdown.LockConfidence, 12);
        Assert.InRange(breakdown.EvidenceConfidence, 0.16d, 0.17d);
        Assert.Contains("OccupancyCoverageBelowMinimum", breakdown.LowEvidenceReason);
        Assert.Contains("InconsistentPartitions", breakdown.LowEvidenceReason);
    }

    [Fact]
    public void WeakFeatureClusterIsDiagnosticAndDoesNotReduceLockConfidence()
    {
        var tuning = new MapStructureRegistrationTuning();
        tuning.Normalize();
        var withoutFeatures = new MapStructureCandidate
        {
            ChamferPixels = 2.17d,
            EdgeCoverage = 0.73d,
            OccupancyCoverage = 0.88d,
            ConsistentPartitions = 4,
            IsWithinValidBounds = true,
            PriorAgreement = 1d
        };
        var weakFeatures = withoutFeatures with
        {
            FeatureInlierCount = 5,
            FeatureConsensus = 0.25d
        };

        var baseline = MapStructureConfidenceCalculator.Calculate(
            withoutFeatures,
            0.25d,
            tuning);
        var observed = MapStructureConfidenceCalculator.Calculate(
            weakFeatures,
            0.25d,
            tuning);

        Assert.Equal(baseline.LockConfidence, observed.LockConfidence, 12);
        Assert.True(observed.EvidenceConfidence < baseline.EvidenceConfidence);
        Assert.Contains("WeakFeatureConsensus", observed.LowEvidenceReason);
    }

    [Fact]
    public void RepresentativePerfectVisualAlignmentDoesNotCrossRuntimeLockThreshold()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 3.2d
        };
        tuning.Normalize();
        var candidate = new MapStructureCandidate
        {
            ChamferPixels = 3.2d * (1d - 0.2336d),
            EdgeCoverage = 0.7067d,
            OccupancyCoverage = 0.8766d,
            ConsistentPartitions = 4,
            IsWithinValidBounds = true,
            PriorAgreement = 1d
        };

        var breakdown = MapStructureConfidenceCalculator.Calculate(
            candidate,
            0.3492d,
            tuning);

        Assert.Equal(0.9280166666666667d, breakdown.EvidenceConfidence, 12);
        Assert.Equal(0.50205d, breakdown.GeometricLockConfidence, 12);
        Assert.Equal(0.6437611111111111d, breakdown.LockConfidence, 12);
        Assert.True(breakdown.LockConfidence >= MapSessionRules.MediumConfidence);
    }

    [Fact]
    public void GenericStructureRecognitionCarriesExactThirdFloorKey()
    {
        var map = new MapRecord { Id = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow };
        map.Recognition.EnsureStandardAnchors();
        var third = new FloorRecognitionProfile
        {
            FloorKey = "basement",
            RecognitionPixelWidth = 900,
            RecognitionPixelHeight = 700
        };
        map.Recognition.Floors[third.FloorKey] = third;
        map.Floors.Add(new FloorDefinition
        {
            Key = third.FloorKey,
            DisplayName = "Basement",
            SortOrder = 3
        });
        var transform = new MapOverlayTransform
        {
            ScaleX = 1.1d,
            ScaleY = 1.1d,
            ReferenceWidth = 900,
            ReferenceHeight = 700,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
        var structure = new MapStructureRegistrationResult
        {
            Accepted = true,
            Transform = transform,
            Confidence = 0.88d
        };

        var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(
            map,
            "basement",
            "C:\\fake\\basement.png",
            transform,
            structure);

        Assert.Equal("basement", recognition.Result.Floor);
        Assert.Equal("C:\\fake\\basement.png", recognition.FloorImagePath);
    }

    [Fact]
    public async Task ThreeFloorGateFreeAlignmentUsesEachTargetsOwnReferenceSizeAndTranslation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.MultiFloor.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mainPath = Path.Combine(root, "main.png");
            var upperPath = Path.Combine(root, "upper.png");
            var basementPath = Path.Combine(root, "basement.png");
            using var mainImage = BuildStructuredReference(480, 360, 0);
            using var upperImage = BuildStructuredReference(560, 420, 1);
            using var basementImage = BuildStructuredReference(640, 480, 2);
            Assert.True(Cv2.ImWrite(mainPath, mainImage));
            Assert.True(Cv2.ImWrite(upperPath, upperImage));
            Assert.True(Cv2.ImWrite(basementPath, basementImage));

            var recognition = new MapRecognitionProfile();
            recognition.EnsureStandardAnchors();
            recognition.FirstFloor.FloorKey = "main";
            recognition.SecondFloor.FloorKey = "upper";
            var basement = new FloorRecognitionProfile { FloorKey = "basement" };
            recognition.Floors = new Dictionary<string, FloorRecognitionProfile>
            {
                ["main"] = recognition.FirstFloor,
                ["upper"] = recognition.SecondFloor,
                ["basement"] = basement
            };
            recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.05d, Height = 0.05d };
            recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
                new NormalizedRectangle { X = 0.8d, Y = 0.8d, Width = 0.05d, Height = 0.05d };
            var floors = new List<FloorDefinition>
            {
                new() { Key = "main", DisplayName = "Main", SortOrder = 1 },
                new() { Key = "upper", DisplayName = "Upper", SortOrder = 2 },
                new() { Key = "basement", DisplayName = "Basement", SortOrder = 3 }
            };
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var map = await repository.SaveAsync(new MapDraft
            {
                Floors = floors,
                FloorPaths = new Dictionary<string, string>
                {
                    ["main"] = mainPath,
                    ["upper"] = upperPath,
                    ["basement"] = basementPath
                },
                Recognition = recognition
            });
            using var service = new MapCvRecognitionService(repository);
            await service.RefreshCacheAsync();

            using (var primaryFrame = new CapturedGameFrame(
                mainImage.Clone(),
                new MapScreenRect(0d, 0d, 1920d, 1080d),
                new MapScreenRect(0d, 0d, mainImage.Width, mainImage.Height),
                IntPtr.Zero))
            {
                var rejectedPrimary = service.AlignFloorWithoutGates(
                    primaryFrame,
                    map.Id,
                    "main",
                    new MapOverlayTransform
                    {
                        ScaleX = 1d,
                        ScaleY = 1d,
                        ReferenceWidth = mainImage.Width,
                        ReferenceHeight = mainImage.Height,
                        AlignmentMode = MapOverlayAlignmentMode.Uniform
                    },
                    MapOverlayAlignmentMode.Uniform,
                    new MapRecognitionTuning());
                Assert.Null(rejectedPrimary.Recognition);
                Assert.Contains("double-gate", rejectedPrimary.FailureReason);
                Assert.Equal(0, rejectedPrimary.Diagnostics.GateCandidateCount);
            }

            await AssertFloorAlignmentAsync(
                service,
                map.Id,
                "upper",
                upperImage,
                new Rect(90, 70, 320, 250));
            await AssertFloorAlignmentAsync(
                service,
                map.Id,
                "basement",
                basementImage,
                new Rect(120, 85, 350, 270));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static Task AssertFloorAlignmentAsync(
        MapCvRecognitionService service,
        Guid mapId,
        string floorKey,
        Mat reference,
        Rect crop)
    {
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(700d, 360d, live.Width, live.Height);
        using var frame = new CapturedGameFrame(
            live.Clone(),
            new MapScreenRect(0d, 0d, 1920d, 1080d),
            viewport,
            IntPtr.Zero);
        var tuning = new MapStructureRegistrationTuning
        {
            MinimumEdgePixels = 50,
            MinimumSpanPixels = 18,
            MinimumConsistentPartitions = 2,
            TopCandidateCount = 6,
            MaximumChamferPixels = 3.5d,
            MinimumEdgeCoverage = 0.50d,
            MinimumOccupancyCoverage = 0.35d,
            MinimumCandidateMargin = 0.025d,
            ScaleSearchRadius = 0.15d,
            ScaleSearchStep = 0.01d
        };
        var seed = new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };

        var attempt = service.AlignFloorWithoutGates(
            frame,
            mapId,
            floorKey,
            seed,
            MapOverlayAlignmentMode.Uniform,
            new MapRecognitionTuning { MinimumConfidence = 0.30d },
            tuning);

        Assert.NotNull(attempt.Recognition);
        Assert.Equal(floorKey, attempt.Recognition.Result.Floor);
        Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
        Assert.InRange(
            Math.Abs(attempt.Recognition.Result.OverlayTransform!.OffsetX
                - (viewport.X - crop.X)),
            0d,
            4d);
        Assert.InRange(
            Math.Abs(attempt.Recognition.Result.OverlayTransform.OffsetY
                - (viewport.Y - crop.Y)),
            0d,
            4d);
        return Task.CompletedTask;
    }

    private static Mat BuildStructuredReference(int width, int height, int variant)
    {
        var image = new Mat(new Size(width, height), MatType.CV_8UC3, Scalar.Black);
        void Box(double x, double y, double w, double h) => Cv2.Rectangle(
            image,
            new Rect(
                (int)(x * width),
                (int)(y * height),
                (int)(w * width),
                (int)(h * height)),
            Scalar.White,
            -1);
        Box(0.07, 0.08, 0.19, 0.19);
        Box(0.38, 0.06, 0.25, 0.16);
        Box(0.73, 0.14, 0.14, 0.28);
        Box(0.14, 0.50, 0.17, 0.31);
        Box(0.44, 0.41, 0.20, 0.28);
        Box(0.72, 0.66, 0.20, 0.20);
        Cv2.Line(
            image,
            new Point((int)(0.25 * width), (int)(0.18 * height)),
            new Point((int)(0.40 * width), (int)(0.14 * height)),
            Scalar.White,
            14 + variant * 2);
        Cv2.Line(
            image,
            new Point((int)(0.58 * width), (int)(0.22 * height)),
            new Point((int)(0.55 * width), (int)(0.44 * height)),
            Scalar.White,
            12 + variant * 2);
        Cv2.Circle(
            image,
            new Point((int)(0.54 * width), (int)(0.57 * height)),
            18 + variant * 2,
            Scalar.Black,
            -1);
        return image;
    }

    private static MapFloorScaleCalibration Calibration(
        string floorKey,
        Guid mapId,
        DateTimeOffset updatedAt) => new()
    {
        MapId = mapId,
        MapUpdatedAt = updatedAt,
        PrimaryFloorKey = "primary",
        FloorKey = floorKey
    };
}
