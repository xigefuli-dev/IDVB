using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class FloorIndicatorTemplateRegistryTests
{
    [Theory]
    [InlineData("nightmare", "b1f")]
    [InlineData("nightmare", "1f")]
    [InlineData("nightmare", "2f")]
    [InlineData("hard", "1f")]
    [InlineData("hard", "2f")]
    public void WholeIndicatorSelectsFloorAfterBrightnessAndSizeChange(string key, string floor)
    {
        var group = FloorIndicatorTemplateRegistry.Get(key)!;
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Assets", "FloorIndicators", group.States[floor]));
        using var resized = new Mat();
        using var canvas = new Mat(group.PixelHeight, group.PixelWidth, original.Type(), Scalar.All(30));
        using (var target = new Mat(canvas, new Rect(5, 4, original.Width, original.Height)))
            original.CopyTo(target);
        Cv2.Resize(canvas, resized, new Size(canvas.Width * 2, canvas.Height * 2));
        resized.ConvertTo(resized, -1, 0.7, 12);
        Assert.Equal(floor, FloorIndicatorTemplateRegistry.Recognize(group, resized, out _, out _));
    }

    [Fact]
    public void FullWidthHeaderFindsIndicatorOutsideFormerRegion()
    {
        var group = FloorIndicatorTemplateRegistry.Get("nightmare")!;
        using var template = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Assets", "FloorIndicators", group.States["b1f"]));
        using var header = new Mat(160, 1920, template.Type(), Scalar.All(25));
        using (var target = new Mat(header, new Rect(900, 45, template.Width, template.Height)))
            template.CopyTo(target);
        using var live = new Mat();
        Cv2.Resize(header, live, new Size(2560, 213));
        Assert.Equal("b1f", FloorIndicatorTemplateRegistry.Recognize(group, live,
            out _, out _, 2560d / 1920));

        var viewport = new NormalizedRectangle { X = .2, Y = .2, Width = .7, Height = .7 };
        var top = FloorIndicatorCaptureRegion.Above(viewport);
        var capture = FloorIndicatorCaptureRegion.IncludeMap(viewport);
        Assert.Equal(0, top.X);
        Assert.Equal(0, top.Y);
        Assert.Equal(1, top.Width);
        Assert.Equal(viewport.Y, top.Height);
        Assert.Equal(1, capture.Width);
        Assert.Equal(viewport.Y + viewport.Height, capture.Height);
    }

    [Fact]
    public void BrokenIndicatorDoesNotThrowOrReusePreviousFloor()
    {
        var group = FloorIndicatorTemplateRegistry.Get("hard")!;
        using var valid = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Assets", "FloorIndicators", group.States["2f"]));
        Assert.Equal("2f", FloorIndicatorTemplateRegistry.Recognize(group, valid, out _, out _, 1));
        using var broken = new Mat(80, 176, MatType.CV_8UC2, Scalar.All(30));
        Assert.Null(FloorIndicatorTemplateRegistry.Recognize(group, broken, out var score, out var margin));
        Assert.Equal(0, score);
        Assert.Equal(0, margin);
    }

    [Fact]
    public void RegistryUsesExactFloorKeysAndRejectsEmptyFrame()
    {
        Assert.Equal("nightmare", FloorIndicatorTemplateRegistry.Resolve(["2f", "b1f", "1f"])?.Key);
        Assert.Null(FloorIndicatorTemplateRegistry.Resolve(["1f", "3f"]));
        Assert.Null(FloorIndicatorTemplateRegistry.Get("missing"));
        using var blank = new Mat(60, 220, MatType.CV_8UC3, Scalar.All(30));
        Assert.Null(FloorIndicatorTemplateRegistry.Recognize(
            FloorIndicatorTemplateRegistry.Get("nightmare")!, blank, out _, out _));
    }
}
