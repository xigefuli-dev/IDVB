using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapViewportStabilityTrackerTests
{
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
}
