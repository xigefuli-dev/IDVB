using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceScanPipelineTests
{
    [Fact]
    public void SideFeatureMatchCreatesSeedUsingViewportCoordinates()
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.SideEntranceFeatureRadius = 20;

        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = 0.94d,
            MatchScale = 1d,
            MatchLocation = new MapScreenRect(300d, 150d, 40d, 40d)
        };

        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            new MapScreenRect(100d, 200d, 800d, 600d),
            out var session,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.Equal(map.Id, session.MapId);
        Assert.Equal(1d, session.LockedTransform.ScaleX, 8);
        Assert.Equal(220d, session.LockedTransform.OffsetX, 8);
        Assert.Equal(70d, session.LockedTransform.OffsetY, 8);
        Assert.False(session.HasGatePairLock);
    }

    [Fact]
    public void SeedScaleComesFromSearchedScaleNotTemplateSize()
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.SideEntranceFeatureRadius = 20;

        // The matched rectangle is the scaled template, so its size alone
        // carries no scale information; only MatchScale does.
        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = 0.88d,
            MatchScale = 1.25d,
            MatchLocation = new MapScreenRect(300d, 150d, 50d, 50d)
        };

        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            new MapScreenRect(100d, 200d, 800d, 600d),
            out var session,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.Equal(1.25d, session.LockedTransform.ScaleX, 8);
        Assert.Equal(1.25d, session.LockedTransform.ScaleY, 8);
        Assert.Equal(1.25d, session.BaselineGateScale, 8);
        // screenCenter 425 = 100 + 300 + 25; offset = 425 - 200 * 1.25
        Assert.Equal(175d, session.LockedTransform.OffsetX, 8);
        // screenCenter 375 = 200 + 150 + 25; offset = 375 - 300 * 1.25
        Assert.Equal(0d, session.LockedTransform.OffsetY, 8);
    }

    [Fact]
    public void GateSeedKeepsFeatureScaleAndTranslation()
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
        {
            X = 0.18d,
            Y = 0.35d,
            Width = 0.04d,
            Height = 0.05d
        };
        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = 0.92d,
            MatchScale = 1.25d,
            MatchLocation = new MapScreenRect(300d, 150d, 50d, 50d)
        };
        var gate = new GateDetection
        {
            Score = 0.95d,
            Scale = 0.4d,
            // A measured 40px icon over a 20px reference would imply 2.0.
            // It must not replace the feature-derived map scale of 1.25.
            ScreenBounds = new MapScreenRect(700d, 500d, 40d, 40d)
        };

        var created = SideEntranceScanPipeline.TryCreateGateAlignmentSeed(
            candidate,
            gate,
            new MapScreenRect(100d, 200d, 800d, 600d),
            referenceGateIconWidth: 20d,
            referenceGateIconHeight: 20d,
            out var session,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.Equal(1.25d, session.LockedTransform.ScaleX, 8);
        Assert.Equal(175d, session.LockedTransform.OffsetX, 8);
        Assert.Equal(0d, session.LockedTransform.OffsetY, 8);
        Assert.Equal(1.25d, session.BaselineGateScale, 8);
        Assert.Equal(0.4d, session.GateTemplateScale!.Value, 8);
    }

    [Fact]
    public void SeedRejectsScaleOutsideSearchRange()
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.SideEntranceFeatureRadius = 20;

        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = 0.8d,
            MatchScale = 12d,
            MatchLocation = new MapScreenRect(300d, 150d, 480d, 480d)
        };

        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            new MapScreenRect(100d, 200d, 800d, 600d),
            out _,
            out var failureReason);

        Assert.False(created);
        Assert.Contains("缩放", failureReason);
    }

    [Theory]
    [InlineData(-0.25d, 0d)]
    [InlineData(1.25d, 1d)]
    public void SeedClampsRecognitionConfidence(
        double matchScore,
        double expectedConfidence)
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.SideEntranceFeatureRadius = 20;
        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = matchScore,
            MatchScale = 1d,
            MatchLocation = new MapScreenRect(300d, 150d, 40d, 40d)
        };

        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            new MapScreenRect(100d, 200d, 800d, 600d),
            out var session,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.Equal(expectedConfidence, session.LastConfidence);
        Assert.Equal(expectedConfidence, session.LastObservationConfidence);
        Assert.Equal(
            expectedConfidence,
            session.SideEntranceScanPriorConfidence);
    }

    [Fact]
    public void ScanRecoversTheScaleOfAKnownPlantedFeature()
    {
        using var scene = new StructuralScanScene(scale: 1.4, x: 85, y: 65);
        var candidate = Assert.Single(scene.Scan());
        Assert.InRange(candidate.MatchScale, 1.38, 1.42);
        scene.AssertPose(candidate);
    }

    [Fact]
    public void ScanFindsAFeaturePlantedAgainstTheFrameEdge()
    {
        using var scene = new StructuralScanScene(x: 160, y: 120);
        // The complete reference reaches the right and bottom frame boundaries.
        Assert.Equal(800 + 160, scene.Frame.Width);
        Assert.Equal(600 + 120, scene.Frame.Height);
        scene.AssertPose(Assert.Single(scene.Scan()));
    }

    [Fact]
    public void ScanReturnsNoCandidateForWeakUnrelatedPixels()
    {
        using var scene = new StructuralScanScene();
        scene.Frame.SetTo(new Scalar(30, 25, 22));
        Assert.Empty(scene.Scan());
    }

    [Fact]
    public void IndistinguishableTemplatesRemainReferenceOnly()
    {
        using var scene = new StructuralScanScene();
        var duplicateMap = scene.CreateMatchingMap();
        using var duplicate = scene.Line.Clone();
        var results = new SideEntranceScanPipeline().RunScan(scene.Frame,
            [(scene.Map, "1f", scene.Line), (duplicateMap, "1f", duplicate)],
            detectedGate: scene.Gate, viewportBounds: scene.Viewport);
        Assert.Equal(2, results.Count);
        Assert.InRange(results[0].TemplateMargin, 0, .001);
        foreach (var candidate in results)
        {
            Assert.Equal(SideEntranceCandidateDisposition.NeedsVerification, candidate.Disposition);
            candidate.IdentityEvidence = scene.Verify(candidate);
            Assert.Equal(ScanIdentityState.Supported, candidate.IdentityEvidence.State);
        }
        // Even if subsequent alignment confirms both maps, equal structural
        // evidence cannot establish a unique identity.
        foreach (var candidate in results)
            candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
        Assert.Null(ScanIdentityVerifier.SelectIdentity(results, true, true));
    }

    [Fact]
    public void GateSpatialMismatchCannotBecomeVerifiedCandidate()
    {
        using var scene = new StructuralScanScene();
        var results = scene.Scan(scene.UnrelatedGate);
        Assert.NotEmpty(results); // Retrieval keeps proposals; the verifier rejects wrong poses.
        foreach (var candidate in results)
        {
            Assert.All(candidate.SearchHypotheses, hypothesis =>
                Assert.NotEqual(ScanIdentityState.Supported, scene.Verify(hypothesis).State));
            Assert.Equal(SideEntranceCandidateDisposition.NeedsVerification, candidate.Disposition);
        }
        Assert.Null(ScanIdentityVerifier.SelectIdentity(results, true, true));
    }

    [Fact]
    public void GateConstrainedSearchIgnoresAStrongerRemotePeak()
    {
        using var scene = new StructuralScanScene(x: 850, frameWidth: 1700);
        using var remote = new StructuralScanScene();
        using (var destination = new Mat(scene.Frame, new Rect(0, 0, 800, 600)))
        using (var source = new Mat(remote.Frame, new Rect(0, 0, 800, 600)))
            source.CopyTo(destination);
        // Damage the detected icon only; its shared exclusion must remain neutral.
        Cv2.Rectangle(scene.Frame, new Rect(1238, 288, 24, 24), Scalar.White, -1);
        var candidate = Assert.Single(scene.Scan());
        Assert.InRange(candidate.MatchScale, .98, 1.02);
        Assert.InRange(candidate.MatchLocation.X, 845, 855);
        Assert.InRange(candidate.MatchLocation.Y, -5, 5);
        Assert.InRange(candidate.GateSpatialResidualPixels, 0, Math.Sqrt(18));
        Assert.Same(scene.Gate, candidate.AssociatedGate);
    }

    // Independent authored geometry supplies both a prebuilt binary contour and
    // a colored live room image. Never crop a grayscale template from the frame.
    private sealed class StructuralScanScene : IDisposable
    {
        private static readonly Rect[] Rooms =
        [
            new(80, 130, 200, 140), new(390, 120, 250, 100),
            new(120, 400, 250, 120), new(680, 380, 60, 80),
            new(430, 420, 150, 110)
        ];
        public Mat Line { get; } = new(600, 800, MatType.CV_8UC1, Scalar.Black);
        public Mat Frame { get; }
        public MapRecord Map { get; }
        public GateDetection Gate { get; }
        public MapScreenRect Viewport => new(100, 200, Frame.Width, Frame.Height);
        public GateDetection UnrelatedGate => new()
        {
            Score = .99, Scale = 1,
            ScreenBounds = new MapScreenRect(130, 230, 20, 20)
        };
        private readonly double scale;
        private readonly double x;
        private readonly double y;

        public StructuralScanScene(double scale = 1, int x = 0, int y = 0, int frameWidth = 960)
        {
            this.scale = scale;
            this.x = x;
            this.y = y;
            Frame = new Mat(Math.Max(720, (int)(600 * scale + y)),
                Math.Max(frameWidth, (int)(800 * scale + x)), MatType.CV_8UC3, new Scalar(30, 25, 22));
            foreach (var room in Rooms)
            {
                Cv2.Rectangle(Line, room, Scalar.White, 1);
                Cv2.Rectangle(Frame, new Rect(
                    (int)Math.Round(x + room.X * scale), (int)Math.Round(y + room.Y * scale),
                    (int)Math.Round(room.Width * scale), (int)Math.Round(room.Height * scale)),
                    new Scalar(110, 97, 88), -1);
            }
            Cv2.Rectangle(Line, new Rect(390, 290, 20, 20), Scalar.Black, -1);
            Map = CreateMatchingMap();
            Gate = new GateDetection
            {
                Score = .91, Scale = 1,
                ScreenBounds = new MapScreenRect(100 + x + 400 * scale - 10,
                    200 + y + 300 * scale - 10, 20, 20)
            };
        }

        public MapRecord CreateMatchingMap()
        {
            var map = CreateMap();
            var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
            profile.RecognitionPixelWidth = 800;
            profile.RecognitionPixelHeight = 600;
            profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
            { X = 390d / 800, Y = 290d / 600, Width = 20d / 800, Height = 20d / 600 };
            return map;
        }

        public IReadOnlyList<SideEntranceScanCandidate> Scan(GateDetection? gate = null) =>
            new SideEntranceScanPipeline().RunScan(Frame, [(Map, "1f", Line)],
                detectedGate: gate ?? Gate, viewportBounds: Viewport);

        public ScanIdentityEvidence Verify(SideEntranceScanCandidate candidate)
        {
            using var evidence = new ScanFrameEvidence(Frame, Viewport,
                [candidate.AssociatedGate!], ScanExecutionPolicy.For(ScanPerformanceMode.Balanced));
            Assert.True(SideEntranceScanPipeline.TryCreateAlignmentSeed(candidate, Viewport,
                out var session, out var failure), failure);
            return ScanIdentityVerifier.Verify(evidence, candidate.StructureIndex!,
                session.LockedTransform, Viewport, null);
        }

        public void AssertPose(SideEntranceScanCandidate candidate)
        {
            Assert.InRange(candidate.MatchScale, scale - .02, scale + .02);
            Assert.InRange(candidate.MatchLocation.X, x - 5, x + 5);
            Assert.InRange(candidate.MatchLocation.Y, y - 5, y + 5);
            Assert.Equal(400d, candidate.ReferenceCenterX);
            Assert.Equal(300d, candidate.ReferenceCenterY);
            Assert.True(SideEntranceScanPipeline.TryCreateAlignmentSeed(candidate, Viewport,
                out var session, out var failure), failure);
            Assert.InRange(session.LockedTransform.OffsetX, Viewport.X + x - 5, Viewport.X + x + 5);
            Assert.InRange(session.LockedTransform.OffsetY, Viewport.Y + y - 5, Viewport.Y + y + 5);
            Assert.Equal(ScanIdentityState.Supported, Verify(candidate).State);
        }

        public void Dispose() { Frame.Dispose(); Line.Dispose(); }
    }
    [Fact]
    public void FeaturePreprocessorMasksTheSharedGateGlyph()
    {
        using var image = BuildTexture(240, 240, seed: 131);
        var anchor = new NormalizedRectangle
        {
            X = 0.45d,
            Y = 0.45d,
            Width = 0.10d,
            Height = 0.10d
        };
        Cv2.Rectangle(image, new Rect(108, 108, 24, 24), Scalar.All(255), -1);

        using var result = new SideEntranceFeaturePreprocessor().Process(
            image,
            anchor,
            featureRadius: 60);
        using var icon = new Mat(result.Feature, new Rect(48, 48, 24, 24));
        Cv2.MeanStdDev(icon, out var mean, out var standardDeviation);

        Assert.InRange(standardDeviation.Val0, 0d, 0.001d);
        Assert.True(mean.Val0 < 250d);
        Assert.Equal("6-prebuilt-structure", SideEntranceFeaturePreprocessor.AlgorithmVersion);
    }

    [Fact]
    public void FeaturePreprocessorUsesRecognitionResolutionRatioWithoutClampingCenter()
    {
        using var image = BuildTexture(1000, 500, seed: 141);
        var anchor = new NormalizedRectangle
        {
            X = 0.01d,
            Y = 0.42d,
            Width = 0.04d,
            Height = 0.08d
        };

        using var result = new SideEntranceFeaturePreprocessor().Process(
            image,
            anchor,
            featureRegionRatio: 0.12d,
            clampToBounds: false);

        Assert.Equal(120, result.Feature.Width);
        Assert.Equal(60, result.Feature.Height);
        Assert.Equal(30d, result.CenterX, 8);
        Assert.Equal(230d, result.CenterY, 8);
        Assert.Equal(120, result.Width);
        Assert.Equal(60, result.Height);
    }

    [Fact]
    public void FeaturePreprocessorCanStillClampCenterWhenConfigured()
    {
        using var image = BuildTexture(1000, 500, seed: 142);
        var anchor = new NormalizedRectangle
        {
            X = 0.01d,
            Y = 0.42d,
            Width = 0.04d,
            Height = 0.08d
        };

        using var result = new SideEntranceFeaturePreprocessor().Process(
            image,
            anchor,
            featureRegionRatio: 0.12d,
            clampToBounds: true);

        Assert.Equal(60d, result.CenterX, 8);
        Assert.Equal(230d, result.CenterY, 8);
        Assert.Equal(120, result.Feature.Width);
        Assert.Equal(60, result.Feature.Height);
    }

    /// <summary>
    /// Builds a deterministic texture of random rectangles. The structure has
    /// to be non-periodic, or the template would match the background just as
    /// well as the planted copy and the recovered scale would be arbitrary. It
    /// also has to carry contrast at coarse scales, because the scale search
    /// locates its peak on a 4x downsample.
    /// </summary>
    private static Mat BuildTexture(int width, int height, int seed)
    {
        var image = new Mat(height, width, MatType.CV_8UC1, Scalar.All(128));
        var random = new Random(seed);
        for (var index = 0; index < 90; index++)
        {
            var rectWidth = random.Next(width / 12, width / 4);
            var rectHeight = random.Next(height / 12, height / 4);
            var rect = new Rect(
                random.Next(0, Math.Max(1, width - rectWidth)),
                random.Next(0, Math.Max(1, height - rectHeight)),
                rectWidth,
                rectHeight);
            Cv2.Rectangle(
                image,
                rect,
                Scalar.All(random.Next(0, 256)),
                thickness: -1);
        }
        return image;
    }
}
