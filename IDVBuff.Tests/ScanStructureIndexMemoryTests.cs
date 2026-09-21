using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ScanStructureIndexMemoryTests
{
    [Fact]
    public void DistanceFieldRetainsOneBytePerReferencePixel()
    {
        using var line = new Mat(120, 160, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(line, new Point(80, 0), new Point(80, 119), Scalar.White);

        var index = ScanStructureIndex.Get(line);

        Assert.Equal(line.Width * line.Height, index.RetainedDistanceBytes);
    }

    [Fact]
    public void CompactDistanceFieldPreservesVerificationBands()
    {
        using var line = new Mat(80, 80, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(line, new Point(40, 0), new Point(40, 79), Scalar.White);
        var index = ScanStructureIndex.Get(line);

        Assert.InRange(index.Distance(40, 30, 1), 0, 0.01);
        Assert.InRange(index.Distance(42.5, 30, 1), 2.35, 2.65);
        Assert.InRange(index.Distance(45.5, 30, 1), 5.35, 5.65);
        Assert.InRange(index.Distance(70, 30, 1), 29.8, 30.2);
        Assert.InRange(index.Distance(70, 30, 0.1), 2.98, 3.02);

        using var wideLine = new Mat(80, 140, MatType.CV_8UC1, Scalar.Black);
        Cv2.Line(wideLine, new Point(20, 0), new Point(20, 79), Scalar.White);
        var wideIndex = ScanStructureIndex.Get(wideLine);
        Assert.InRange(wideIndex.Distance(75, 30, 0.1), 5.45, 5.55);
    }
}
