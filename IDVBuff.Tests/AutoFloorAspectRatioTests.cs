using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AutoFloorAspectRatioTests
{
    // 1366x768 and commercial ultrawide panels include their real rounded sizes.
    public static IEnumerable<object[]> Resolutions()
    {
        foreach (var (family, width, height) in new[]
        {
            ("16:9", 1280, 720), ("16:9", 1366, 768), ("16:9", 1600, 900),
            ("16:9", 1920, 1080), ("16:9", 2560, 1440), ("16:9", 3840, 2160),
            ("16:10", 1280, 800), ("16:10", 1440, 900), ("16:10", 1680, 1050),
            ("16:10", 1920, 1200), ("16:10", 2560, 1600), ("16:10", 3840, 2400),
            ("21:9", 1680, 720), ("21:9", 2520, 1080), ("21:9", 2560, 1080),
            ("21:9", 3440, 1440), ("21:9", 3840, 1600), ("21:9", 5120, 2160),
            ("feedback-window", 2880, 1479), ("feedback-video", 1280, 656)
        })
            yield return [family, width, height];
    }

    public static IEnumerable<object[]> FloorCases()
    {
        foreach (var resolution in Resolutions())
        foreach (var (group, floor) in new[]
        {
            ("hard", "1f"), ("hard", "2f"),
            ("nightmare", "b1f"), ("nightmare", "1f"), ("nightmare", "2f")
        })
        foreach (var mapTop in new[] { 0d, .04d, 1d / 6 })
            yield return [.. resolution, group, floor, mapTop];
    }

    [Theory]
    [MemberData(nameof(FloorCases))]
    public void CompleteHeaderSurvivesMapCalibrationAndSelectsEveryFloor(
        string family, int width, int height, string key, string floor, double mapTop)
    {
        var group = FloorIndicatorTemplateRegistry.Get(key)!;
        var viewport = mapTop == 0
            ? new NormalizedRectangle { X = 0, Y = 0, Width = 1, Height = 1 }
            : new NormalizedRectangle { X = .3, Y = mapTop, Width = .6, Height = .75 };
        var headerRegion = FloorIndicatorCaptureRegion.Above(viewport, group);
        var capture = FloorIndicatorCaptureRegion.IncludeMap(viewport, group);
        Assert.True(headerRegion.Height >= group.Y + group.Height);
        Assert.True(capture.Height >= headerRegion.Height);
        Assert.True(capture.Height >= viewport.Y + viewport.Height);
        Assert.Equal(0d, capture.X);
        Assert.Equal(1d, capture.Width);

        // Independently construct a reference-canvas UI, then use the production
        // crop and scale rules. This is synthetic coverage, not live game capture.
        var expectedScale = Math.Min(width / 1920d, height / 1080d);
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Assets", "FloorIndicators", group.States[floor]));
        using var indicator = new Mat();
        Cv2.Resize(original, indicator, new Size(
            (int)Math.Round(original.Width * expectedScale),
            (int)Math.Round(original.Height * expectedScale)), 0, 0, InterpolationFlags.Area);
        var productionScale = FloorIndicatorCaptureRegion.TemplateScale(group,
            new MapScreenRect(-700, 135, width, height));
        Assert.Equal(expectedScale, productionScale, 6);

        // Exercise shifted UI outside the old right-hand ROI and the right edge;
        // the second case is dimmed, as during the map's opening animation.
        foreach (var shifted in new[] { false, true })
        {
            using var header = new Mat((int)Math.Ceiling(headerRegion.Height * height),
                width, original.Type(), Scalar.All(25));
            var left = shifted ? (int)(width * .15) : width - indicator.Width - 8;
            var top = (int)Math.Round(40 * expectedScale);
            var button = new Rect(left, top, indicator.Width, indicator.Height);
            Assert.True(button.Bottom <= header.Height,
                $"{family} {width}x{height} {key}/{floor}: truncated header");
            using (var target = new Mat(header, button))
                indicator.CopyTo(target);
            if (shifted)
                header.ConvertTo(header, -1, .65, 15);
            var result = FloorIndicatorTemplateRegistry.RecognizeDetailed(group, header, productionScale);
            Assert.True(result.DetectedFloor == floor,
                $"{family} {width}x{height} {key}/{floor}, Y={mapTop}: "
                + $"detected={result.DetectedFloor}, score={result.BestScore}, {result.RejectionReason}");
        }
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void RealVideoHeaderReprojectedWithoutAspectStretchStillSelectsFirstFloor(
        string family, int width, int height)
    {
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "auto-floor-1280x656-header.png"), ImreadModes.Grayscale);
        // Isotropic UI resizing preserves glyph shape; stretching the entire
        // video to another aspect ratio would test an impossible game image.
        var scale = Math.Min(width / 1920d, height / 1080d);
        var ratio = scale / (656d / 1080);
        using var patch = new Mat();
        Cv2.Resize(original, patch, new Size((int)Math.Round(original.Width * ratio),
            (int)Math.Round(original.Height * ratio)), 0, 0, InterpolationFlags.Area);
        using var header = new Mat(patch.Height, width, MatType.CV_8UC1, Scalar.All(25));
        using (var target = new Mat(header, new Rect(width - patch.Width, 0, patch.Width, patch.Height)))
            patch.CopyTo(target);
        var group = FloorIndicatorTemplateRegistry.Get("hard")!;
        var result = FloorIndicatorTemplateRegistry.RecognizeDetailed(group, header,
            FloorIndicatorCaptureRegion.TemplateScale(group, new MapScreenRect(0, 0, width, height)));
        Assert.True(result.DetectedFloor == "1f",
            $"{family} {width}x{height}: {result.DetectedFloor}, {result.BestScore}");
    }

    [Fact]
    public void ExpandedCaptureContainsHeaderEvenWhenMapCalibrationEndsAboveIt()
    {
        var viewport = new NormalizedRectangle { X = .3, Y = .01, Width = .2, Height = .04 };
        var group = FloorIndicatorTemplateRegistry.Get("nightmare")!;
        Assert.True(FloorIndicatorCaptureRegion.Above(viewport).Height < group.Y + group.Height);
        var header = FloorIndicatorCaptureRegion.Above(viewport, group);
        var capture = FloorIndicatorCaptureRegion.IncludeMap(viewport, group);
        Assert.True(header.Height >= group.Y + group.Height);
        Assert.Equal(header.Height, capture.Height);
    }
}
