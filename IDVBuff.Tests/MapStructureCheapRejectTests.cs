using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructureCheapRejectTests
{
    [Fact]
    public void MatchingSeedPassesCheapRejectWithinBudget()
    {
        using var reference = CreateFeatures(10, 10);
        using var live = CreateFeatures(10, 10);
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out var elapsedMilliseconds,
            out var reason);

        Assert.False(rejected, reason);
        Assert.InRange(elapsedMilliseconds, 0d, 50d);
    }

    [Fact]
    public void DistantSeedIsRejectedBeforeFormalRegistration()
    {
        using var reference = CreateFeatures(70, 70);
        using var live = CreateFeatures(10, 10);
        using var liveImage = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        var rejected = MapStructureCheapReject.TryReject(
            CreateRequest(liveImage),
            reference,
            live,
            out var elapsedMilliseconds,
            out var reason);

        Assert.True(rejected, reason);
        Assert.Contains("cheap-reject", reason);
        Assert.InRange(elapsedMilliseconds, 0d, 50d);
    }

    private static MapStructureRegistrationRequest CreateRequest(Mat live) => new()
    {
        LiveRoi = live,
        ViewportBounds = new MapScreenRect(0d, 0d, 128d, 128d),
        LockedTransform = new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = 0d,
            OffsetY = 0d,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        },
        Tuning = new MapStructureRegistrationTuning
        {
            PreviousAlignmentSearchRadiusPixels = 8
        }
    };

    private static MapStructureFeatures CreateFeatures(int x, int y)
    {
        var edges = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(
            edges,
            new Rect(x, y, 48, 36),
            Scalar.All(255),
            thickness: 2);
        Cv2.Line(
            edges,
            new Point(x, y + 60),
            new Point(x + 80, y + 60),
            Scalar.All(255),
            thickness: 2);
        return new MapStructureFeatures(
            new Mat(edges.Size(), MatType.CV_8UC1, Scalar.All(0)),
            edges.Clone(),
            edges);
    }
}
