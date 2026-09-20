using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed partial class MapStructureRegistrarTests
{
    [Fact]
    public void FastOversizedRejectionReportsReferenceAndQueryGeometry()
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
        var tuning = TestFastTuning();
        tuning.FastFallbackToLegacy = false;

        var result = new MapStructureRegistrar(new MapStructurePreprocessor())
            .Register(new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = live,
                ViewportBounds =
                    new MapScreenRect(0d, 0d, live.Width, live.Height),
                LockedTransform = Locked(reference),
                Tuning = tuning,
                AllowScaleSearch = false
            });

        Assert.Equal(
            MapStructureRejectionReason.QueryLargerThanReference,
            result.RejectionReason);
        Assert.Equal(reference.Width, result.ReferenceWidth);
        Assert.Equal(reference.Height, result.ReferenceHeight);
        Assert.True(result.QueryEdgePixels > 0);
        Assert.True(result.QueryBoundsWidth >= result.ReferenceWidth
            || result.QueryBoundsHeight >= result.ReferenceHeight);
    }
}
