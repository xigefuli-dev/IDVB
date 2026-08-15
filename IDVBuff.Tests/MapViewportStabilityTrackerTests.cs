using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapViewportStabilityTrackerTests
{
    [Fact]
    public void PresenceDetectorAcceptsBlueGrayMapAndRejectsBrownGameplay()
    {
        using var map = CreateHsvFrame(new Scalar(108, 100, 50));
        using var gameplay = CreateHsvFrame(new Scalar(15, 170, 100));

        var mapResult = MapViewportPresenceDetector.Evaluate(map);
        var gameplayResult = MapViewportPresenceDetector.Evaluate(gameplay);

        Assert.True(mapResult.IsPresent);
        Assert.False(gameplayResult.IsPresent);
        Assert.Equal("blue-gray-fallback", mapResult.Mode);
    }

    [Fact]
    public void PresenceDetectorUsesReliableReferenceInsteadOfBrightness()
    {
        using var referenceFrame = CreateHsvFrame(new Scalar(108, 100, 50));
        using var changedMap = CreateHsvFrame(new Scalar(108, 100, 90));
        using var darkGameplay = CreateHsvFrame(new Scalar(15, 170, 45));
        var reference = MapViewportPresenceDetector.CreateSignature(referenceFrame);

        var mapResult = MapViewportPresenceDetector.Evaluate(changedMap, reference);
        var gameplayResult = MapViewportPresenceDetector.Evaluate(
            darkGameplay,
            reference);

        Assert.True(mapResult.IsPresent);
        Assert.False(gameplayResult.IsPresent);
        Assert.Equal("reference-hsv", mapResult.Mode);
        Assert.True(mapResult.Score >= MapViewportPresenceDetector.MinimumReferenceSimilarity);
        Assert.True(gameplayResult.Score < MapViewportPresenceDetector.MinimumReferenceSimilarity);
    }

    [Fact]
    public void RequiresThreeConsecutiveStableFrames()
    {
        using var frame = new Mat(
            new Size(320, 200),
            MatType.CV_8UC3,
            new Scalar(40, 40, 40));
        using var tracker = new MapViewportStabilityTracker();

        Assert.False(tracker.Observe(frame, 0.015d, 3));
        Assert.False(tracker.Observe(frame, 0.015d, 3));
        Assert.True(tracker.Observe(frame, 0.015d, 3));
    }

    [Fact]
    public void ConfiguredDynamicRegionDoesNotBreakStability()
    {
        using var first = new Mat(
            new Size(320, 200),
            MatType.CV_8UC3,
            new Scalar(40, 40, 40));
        using var changed = first.Clone();
        Cv2.Rectangle(
            changed,
            new Rect(120, 70, 80, 60),
            Scalar.White,
            -1);
        using var tracker = new MapViewportStabilityTracker();
        var ignore =
            new[]
            {
                new NormalizedRectangle
                {
                    X = 0.35d,
                    Y = 0.30d,
                    Width = 0.30d,
                    Height = 0.40d
                }
            };

        Assert.False(tracker.Observe(first, 0.015d, 3, ignore));
        Assert.False(tracker.Observe(changed, 0.015d, 3, ignore));
        Assert.True(tracker.Observe(first, 0.015d, 3, ignore));
    }

    private static Mat CreateHsvFrame(Scalar hsvColor)
    {
        using var hsv = new Mat(
            new Size(320, 200),
            MatType.CV_8UC3,
            hsvColor);
        var bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        return bgr;
    }
}
