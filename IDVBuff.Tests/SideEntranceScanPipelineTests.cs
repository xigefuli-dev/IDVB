using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class SideEntranceScanPipelineTests
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
        using var reference = BuildTexture(240, 240, seed: 7);
        // Cut a 64x64 template, then plant it into the frame at 1.4x so the
        // search has a single unambiguous correct answer.
        using var template = new Mat(reference, new Rect(40, 60, 64, 64));
        const double plantedScale = 1.4d;
        var plantedSize = (int)Math.Round(64 * plantedScale);
        using var frame = BuildTexture(700, 620, seed: 23);
        using var plantedTemplate = new Mat();
        Cv2.Resize(
            template,
            plantedTemplate,
            new Size(plantedSize, plantedSize),
            0d,
            0d,
            InterpolationFlags.Cubic);
        var target = new Rect(210, 150, plantedSize, plantedSize);
        using (var destination = new Mat(frame, target))
            plantedTemplate.CopyTo(destination);

        var pipeline = new SideEntranceScanPipeline();
        var results = pipeline.RunScan(
            frame,
            [(CreateMap(), "1f", template)]);

        var match = Assert.Single(results);
        // The coarse grid steps by 6% and refines at 1.5%, so the recovered
        // scale lands near 1.4 rather than exactly on it.
        Assert.InRange(match.MatchScale, 1.32d, 1.48d);
        // Refinement runs inside a window around the coarse peak, so the
        // location has to be translated back to frame coordinates. A window
        // origin left in there would show up as a large offset here.
        Assert.InRange(match.MatchLocation.X, target.X - 12d, target.X + 12d);
        Assert.InRange(match.MatchLocation.Y, target.Y - 12d, target.Y + 12d);
        Assert.True(
            match.MatchScore > 0.9d,
            $"planted feature should match strongly but scored {match.MatchScore:F3}");
    }

    /// <summary>
    /// The planted feature sits near the frame's right edge, so the refine
    /// window has to be clamped back inside the frame. An unclamped window
    /// would throw out of OpenCV or silently shift the reported location.
    /// </summary>
    [Fact]
    public void ScanFindsAFeaturePlantedAgainstTheFrameEdge()
    {
        using var reference = BuildTexture(240, 240, seed: 11);
        using var template = new Mat(reference, new Rect(30, 40, 64, 64));
        using var frame = BuildTexture(420, 400, seed: 29);
        var target = new Rect(frame.Width - 64, frame.Height - 64, 64, 64);
        using (var destination = new Mat(frame, target))
            template.CopyTo(destination);

        var pipeline = new SideEntranceScanPipeline();
        var results = pipeline.RunScan(
            frame,
            [(CreateMap(), "1f", template)]);

        var match = Assert.Single(results);
        Assert.InRange(match.MatchLocation.X, target.X - 12d, target.X + 12d);
        Assert.InRange(match.MatchLocation.Y, target.Y - 12d, target.Y + 12d);
        Assert.True(
            match.MatchScore > 0.9d,
            $"edge feature should match strongly but scored {match.MatchScore:F3}");
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

    [Fact]
    public void SideFeatureSeedFallsBackToMarkedSideEntranceCenter()
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 0d;
        profile.SideEntranceFeatureCenterY = 0d;
        profile.SideEntranceFeatureRadius = 0;
        profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
        {
            X = 0.2d,
            Y = 0.3d,
            Width = 0.1d,
            Height = 0.1d
        };

        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = 0.91d,
            MatchScale = 1d,
            MatchLocation = new MapScreenRect(100d, 120d, 60d, 60d)
        };

        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            new MapScreenRect(20d, 30d, 600d, 400d),
            out var session,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.Equal(1d, session.LockedTransform.ScaleX, 8);
        Assert.Equal(-100d, session.LockedTransform.OffsetX, 8);
        Assert.Equal(-100d, session.LockedTransform.OffsetY, 8);
    }

    private static MapRecord CreateMap()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Floors =
            [
                new FloorDefinition
                {
                    Key = "1f",
                    DisplayName = "1F",
                    SortOrder = 1
                }
            ],
            Recognition = new MapRecognitionProfile
            {
                FirstFloor = new FloorRecognitionProfile
                {
                    FloorKey = "1f",
                    RecognitionPixelWidth = 1000,
                    RecognitionPixelHeight = 800
                }
            }
        };
        map.NormalizeRecognition();
        return map;
    }
}
