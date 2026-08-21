using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class StructureFillTests
{
    [Fact]
    public void KeepsEnclosedBackgroundBlackAndSeparateStructures()
    {
        using var source = new Mat(new Size(640, 480), MatType.CV_8UC3, new Scalar(25, 30, 38));
        Cv2.Rectangle(source, new Rect(100, 90, 230, 190), new Scalar(120, 125, 135), -1);
        Cv2.Rectangle(source, new Rect(120, 105, 10, 10), Scalar.Black, -1);
        Cv2.Rectangle(source, new Rect(145, 130, 55, 65), Scalar.Black, -1);
        Cv2.Rectangle(source, new Rect(430, 250, 90, 80), new Scalar(110, 115, 125), -1);
        Cv2.Rectangle(source, new Rect(20, 20, 12, 12), Scalar.White, -1);
        Cv2.Rectangle(source, new Rect(0, 0, source.Width, source.Height), Scalar.White, 1);

        using var result = new MapStructureFiller().Analyze(source);

        Assert.True(result.HasStructure);
        Assert.Equal(0, result.Mask.At<byte>(110, 125));
        Assert.Equal(0, result.Mask.At<byte>(155, 170));
        Assert.NotEqual(0, result.Mask.At<byte>(110, 110));
        Assert.NotEqual(0, result.Mask.At<byte>(275, 470));
        Assert.Equal(0, result.Mask.At<byte>(25, 25));
        Assert.Equal(2, result.ComponentCount);
    }

    [Fact]
    public void FillReturnsAnOwnedSingleChannelMask()
    {
        using var source = new Mat(new Size(320, 240), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(source, new Rect(50, 40, 180, 120), Scalar.White, -1);

        using var mask = new MapStructureFiller().Fill(source);

        Assert.Equal(MatType.CV_8UC1, mask.Type());
        Assert.Equal(source.Size(), mask.Size());
        Assert.True(Cv2.CountNonZero(mask) > 0);
    }

    [Fact]
    public void GuideMapProfileKeepsLongSaturatedLines()
    {
        using var source = new Mat(new Size(640, 480), MatType.CV_8UC3, new Scalar(25, 30, 38));
        Cv2.Line(
            source,
            new Point(70, 220),
            new Point(560, 220),
            new Scalar(255, 255, 0),
            thickness: 5);

        using var result = new MapStructureFiller().Analyze(
            source,
            new StructureFillOptions { ApplyGuideMapTone = true });

        Assert.NotEqual(0, result.Mask.At<byte>(220, 300));
    }
}
