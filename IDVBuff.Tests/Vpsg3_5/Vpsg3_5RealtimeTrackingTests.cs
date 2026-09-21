using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3_5;

public sealed class Vpsg3_5RealtimeTrackingTests
{
    [Fact]
    public void TimestampedMouseHistory_ReconstructsMotionAfterCapture()
    {
        var history = new TimestampedMouseHistory();
        history.Reset(100, 100, 1_000);
        history.Record(110, 104, 2_000, 1d);
        history.Record(130, 110, 3_000, 1d);

        var after = history.DeltaAfter(2_500);

        Assert.Equal(10d, after.Dx, 6);
        Assert.Equal(3d, after.Dy, 6);
    }

    [Fact]
    public void PyramidalLk_TracksSyntheticFrameTranslation()
    {
        using var first = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black);
        for (var y = 20; y < 220; y += 25)
        {
            for (var x = 20; x < 300; x += 25)
                Cv2.Circle(first, new Point(x, y), 3, Scalar.White, -1);
        }
        using var second = new Mat();
        using var affine = Mat.FromArray(new double[,] { { 1d, 0d, 7d }, { 0d, 1d, -4d } });
        Cv2.WarpAffine(first, second, affine, first.Size());
        using var tracker = new Vpsg3_5OpticalFlowTracker();

        var primed = tracker.Track(first);
        var tracked = tracker.Track(second);

        Assert.False(primed.Accepted);
        Assert.True(tracked.Accepted, tracked.RejectionReason);
        Assert.InRange(tracked.DeltaX, 6.5d, 7.5d);
        Assert.InRange(tracked.DeltaY, -4.5d, -3.5d);
        Assert.True(tracked.TrackedPoints >= 12);
    }
}
