using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapBackgroundProcessorTests
{
    [Fact]
    public void RasterizeMaskSupportsCircleSquareAndUnion()
    {
        var layers = new[]
        {
            new MapBackgroundLayer
            {
                Shape = MapBackgroundLayerShape.Circle,
                BrushSizePixels = 3,
                Points = [new NormalizedPoint { X = .25, Y = .25 }]
            },
            new MapBackgroundLayer
            {
                Shape = MapBackgroundLayerShape.Square,
                BrushSizePixels = 3,
                Points = [new NormalizedPoint { X = .75, Y = .75 }]
            }
        };

        using var mask = MapBackgroundProcessor.RasterizeMask(layers, 9, 9);

        Assert.NotEqual(0, mask.At<byte>(2, 2));
        Assert.NotEqual(0, mask.At<byte>(6, 6));
        Assert.Equal(0, mask.At<byte>(0, 8));
    }

    [Fact]
    public void AutomaticRemovalUsesRgbToleranceAndClearsAllChannels()
    {
        using var source = new Mat(1, 5, MatType.CV_8UC4, Scalar.Black);
        SetPixel(source, 0, 0, new Scalar(10, 20, 30, 255));
        SetPixel(source, 0, 1, new Scalar(10, 20, 38, 255));
        SetPixel(source, 0, 2, new Scalar(10, 20, 39, 255));
        SetPixel(source, 0, 3, new Scalar(220, 40, 70, 255));
        SetPixel(source, 0, 4, new Scalar(220, 40, 70, 0));
        var profile = new FloorRecognitionProfile();

        using var result = MapBackgroundProcessor.Process(source, profile, removeBackground: true);

        Assert.Equal(new Vec4b(0, 0, 0, 0), result.Recognition.At<Vec4b>(0, 0));
        Assert.Equal(new Vec4b(0, 0, 0, 0), result.Recognition.At<Vec4b>(0, 1));
        Assert.Equal(new Vec4b(10, 20, 39, 255), result.Recognition.At<Vec4b>(0, 2));
        Assert.Equal(new Vec4b(220, 40, 70, 255), result.Recognition.At<Vec4b>(0, 3));
        Assert.Equal(new Vec4b(220, 40, 70, 0), result.Recognition.At<Vec4b>(0, 4));
    }

    [Fact]
    public void HigherAutomaticRemovalIntensityCoversWiderColorRange()
    {
        using var source = new Mat(1, 2, MatType.CV_8UC4, Scalar.Black);
        SetPixel(source, 0, 0, new Scalar(10, 20, 30, 255));
        SetPixel(source, 0, 1, new Scalar(10, 20, 42, 255));
        var profile = new FloorRecognitionProfile();

        using var low = MapBackgroundProcessor.Process(
            source, profile, removeBackground: true, backgroundRemovalIntensity: 8);
        using var high = MapBackgroundProcessor.Process(
            source, profile, removeBackground: true, backgroundRemovalIntensity: 12);

        Assert.Equal(new Vec4b(10, 20, 42, 255), low.Recognition.At<Vec4b>(0, 1));
        Assert.Equal(new Vec4b(0, 0, 0, 0), high.Recognition.At<Vec4b>(0, 1));
    }

    [Fact]
    public void ManualConcealIsAppliedWhenClassRemovalIsDisabled()
    {
        using var source = new Mat(5, 5, MatType.CV_8UC4, new Scalar(11, 22, 33, 255));
        var profile = new FloorRecognitionProfile
        {
            BackgroundLayers =
            [
                new MapBackgroundLayer
                {
                    Shape = MapBackgroundLayerShape.Square,
                    BrushSizePixels = 1,
                    Points = [new NormalizedPoint { X = .5, Y = .5 }]
                }
            ]
        };

        using var result = MapBackgroundProcessor.Process(source, profile, removeBackground: false);

        Assert.Equal(new Vec4b(0, 0, 0, 0), result.Recognition.At<Vec4b>(2, 2));
        Assert.Equal(new Vec4b(11, 22, 33, 255), result.Recognition.At<Vec4b>(0, 0));
    }

    [Fact]
    public void ConcealStrokeBuilderCreatesOneLayerAndInterpolatesAtQuarterBrushSpacing()
    {
        var builder = new MapConcealStrokeBuilder();
        builder.Begin(
            new NormalizedPoint { X = 0, Y = .5 },
            MapBackgroundLayerShape.Circle,
            20,
            imageWidth: 100,
            imageHeight: 100);
        builder.AddPoint(new NormalizedPoint { X = 1, Y = .5 });

        var layer = builder.Complete();

        Assert.NotNull(layer);
        Assert.Equal(0, layer!.Points[0].X);
        Assert.True(layer.Points.Count >= 21);
        Assert.False(builder.IsActive);
    }

    private static void SetPixel(Mat image, int row, int column, Scalar bgra) =>
        Cv2.Rectangle(image, new Rect(column, row, 1, 1), bgra, -1);
}
