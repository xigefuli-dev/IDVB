using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceScanPipelineTests
{
    [Fact]
    public void MultiGateScanAssociatesCandidateWithItsOwnGate()
    {
        using var template = BuildTexture(64, 64, seed: 151);
        using var frame = BuildTexture(1200, 440, seed: 157);
        using (var target = new Mat(frame, new Rect(900, 140, 64, 64)))
            template.CopyTo(target);
        Cv2.Rectangle(frame, new Rect(922, 162, 20, 20), Scalar.All(128), -1);

        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.SideEntranceFeatureRadius = 32;
        profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
        {
            X = 0.19d,
            Y = 0.3625d,
            Width = 0.02d,
            Height = 0.025d
        };
        var unrelated = new GateDetection
        {
            Score = 0.97d,
            Scale = 1d,
            ScreenBounds = new MapScreenRect(80d, 340d, 20d, 20d)
        };
        var correct = new GateDetection
        {
            Score = 0.91d,
            Scale = 1d,
            ScreenBounds = new MapScreenRect(922d, 162d, 20d, 20d)
        };

        var results = new SideEntranceScanPipeline().RunScan(
            frame,
            [(map, "1f", template)],
            detectedGates: [unrelated, correct],
            topK: 1,
            viewportBounds: new MapScreenRect(0d, 0d, frame.Width, frame.Height));

        var candidate = Assert.Single(results);
        Assert.Same(correct, candidate.AssociatedGate);
        Assert.Equal(1, candidate.AssociatedGateIndex);
        Assert.Equal(
            SideEntranceGateAssociationKind.DetectedGate,
            candidate.GateAssociationKind);
        Assert.InRange(candidate.GateSpatialResidualPixels, 0d, 20d);
    }

    [Fact]
    public void MultiGateScanRescuesStrongTemplateWhenNoGateAssociationIsValid()
    {
        using var template = BuildTexture(64, 64, seed: 163);
        using var frame = BuildTexture(520, 440, seed: 167);
        using (var target = new Mat(frame, new Rect(80, 70, 64, 64)))
            template.CopyTo(target);

        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 200d;
        profile.SideEntranceFeatureCenterY = 300d;
        profile.SideEntranceFeatureRadius = 32;
        profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
        {
            X = 0.19d,
            Y = 0.3625d,
            Width = 0.02d,
            Height = 0.025d
        };
        var unrelated = new GateDetection
        {
            Score = 0.96d,
            Scale = 1d,
            ScreenBounds = new MapScreenRect(470d, 390d, 20d, 20d)
        };

        var results = new SideEntranceScanPipeline().RunScan(
            frame,
            [(map, "1f", template)],
            detectedGates: [unrelated],
            topK: 1,
            viewportBounds: new MapScreenRect(0d, 0d, frame.Width, frame.Height));

        var candidate = Assert.Single(results);
        Assert.Null(candidate.AssociatedGate);
        Assert.Equal(-1, candidate.AssociatedGateIndex);
        Assert.Equal(
            SideEntranceGateAssociationKind.TemplateOnlyRescue,
            candidate.GateAssociationKind);
        Assert.True(candidate.MatchScore >= 0.68d);
        Assert.True(double.IsPositiveInfinity(
            candidate.GateSpatialResidualPixels));
    }

    [Fact]
    public void GateMaskUsesOriginalMeanForEveryGateWithViewportOffset()
    {
        using var frame = BuildTexture(260, 220, seed: 173);
        var originalMean = Cv2.Mean(frame).Val0;
        var viewport = new MapScreenRect(100d, 200d, frame.Width, frame.Height);
        var gates = new[]
        {
            new GateDetection
            {
                Score = 0.95d,
                ScreenBounds = new MapScreenRect(120d, 230d, 18d, 16d)
            },
            new GateDetection
            {
                Score = 0.93d,
                ScreenBounds = new MapScreenRect(280d, 330d, 22d, 20d)
            }
        };

        SideEntranceScanPipeline.MaskDetectedGates(frame, gates, viewport);

        using var first = new Mat(frame, new Rect(20, 30, 18, 16));
        using var second = new Mat(frame, new Rect(180, 130, 22, 20));
        Cv2.MeanStdDev(first, out var firstMean, out var firstStdDev);
        Cv2.MeanStdDev(second, out var secondMean, out var secondStdDev);
        Assert.InRange(firstStdDev.Val0, 0d, 0.001d);
        Assert.InRange(secondStdDev.Val0, 0d, 0.001d);
        Assert.InRange(firstMean.Val0, originalMean - 1d, originalMean + 1d);
        Assert.InRange(secondMean.Val0, originalMean - 1d, originalMean + 1d);
        Assert.Equal(firstMean.Val0, secondMean.Val0, 8);
    }
}
