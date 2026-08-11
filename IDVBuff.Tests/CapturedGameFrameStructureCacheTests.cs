using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class CapturedGameFrameStructureCacheTests
{
    [Fact]
    public void DefaultLiveStructureIsExtractedOnlyOncePerFrozenFrame()
    {
        var image = new Mat(
            new Size(320, 240),
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(image, new Rect(40, 35, 220, 160), Scalar.White, -1);
        using var frame = new CapturedGameFrame(
            image,
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            new MapScreenRect(0d, 0d, image.Width, image.Height),
            IntPtr.Zero);
        var preprocessor = new MapStructurePreprocessor();

        var first = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var firstHit,
            out var firstMilliseconds,
            out var firstTiming);
        var second = frame.GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            out var secondHit,
            out var secondMilliseconds,
            out var secondTiming);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);
        Assert.Same(firstTiming, secondTiming);
        Assert.Equal(firstMilliseconds, secondMilliseconds);
        Assert.True(firstMilliseconds >= 0d);
        Assert.True(firstTiming.StructureComponentCount > 0);
        Assert.True(firstTiming.KeptStructureBoundsWidth > 0);
        Assert.True(firstTiming.KeptStructureBoundsHeight > 0);
    }
}
