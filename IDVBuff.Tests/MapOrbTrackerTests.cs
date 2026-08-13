using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class MapOrbTrackerTests
{
    [Fact]
    public void TracksTranslationAndUniformScaleWithinSyntheticErrorBudget()
    {
        using var source = CreateFeatureRichFrame();
        const double stepScale = 1.006;
        const double dx = 7.0;
        const double dy = -5.0;
        using var target = Warp(source, stepScale, dx, dy);
        var viewport = new MapScreenRect(100, 200, source.Width, source.Height);
        var initial = CreateTransform(scale: 0.8, offsetX: 130, offsetY: 260);
        using var tracker = new MapOrbTracker(
            source,
            viewport,
            initial,
            new MapOrbTrackingOptions());

        var result = tracker.Track(target, viewport, TimeSpan.FromMilliseconds(100));

        Assert.True(result.Accepted, result.RejectionReason);
        Assert.True(result.ShouldCommit);
        var expected = MapOrbTracker.Compose(initial, viewport, stepScale, dx, dy);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - expected.OffsetX), 0, 1.0);
        Assert.InRange(Math.Abs(result.Transform.OffsetY - expected.OffsetY), 0, 1.0);
        Assert.InRange(
            Math.Abs((result.Transform.ScaleX / expected.ScaleX) - 1d),
            0,
            0.003);
        Assert.Equal(result.Transform.ScaleX, result.Transform.ScaleY, 8);
        Assert.InRange(Math.Abs(result.EstimatedRotationDegrees), 0, 0.5);
    }

    [Fact]
    public void RejectedLowFeatureFrameDoesNotReplaceTrustedKeyframe()
    {
        using var source = CreateFeatureRichFrame();
        using var blank = Mat.Zeros(source.Size(), MatType.CV_8UC3).ToMat();
        using var translated = Warp(source, 1, 8, 4);
        var viewport = new MapScreenRect(0, 0, source.Width, source.Height);
        var initial = CreateTransform(1, 20, 30);
        using var tracker = new MapOrbTracker(
            source,
            viewport,
            initial,
            new MapOrbTrackingOptions());

        var rejected = tracker.Track(blank, viewport, TimeSpan.FromMilliseconds(100));
        var recovered = tracker.Track(translated, viewport, TimeSpan.FromMilliseconds(100));

        Assert.False(rejected.Accepted);
        Assert.Same(initial, rejected.Transform);
        Assert.True(recovered.Accepted, recovered.RejectionReason);
        Assert.InRange(Math.Abs(recovered.Transform.OffsetX - 28), 0, 1.0);
        Assert.InRange(Math.Abs(recovered.Transform.OffsetY - 34), 0, 1.0);
    }

    [Fact]
    public void RejectsRotationAboveRenderModelLimit()
    {
        using var source = CreateFeatureRichFrame();
        using var target = Rotate(source, 2.0);
        var viewport = new MapScreenRect(0, 0, source.Width, source.Height);
        var initial = CreateTransform(1, 0, 0);
        using var tracker = new MapOrbTracker(
            source,
            viewport,
            initial,
            new MapOrbTrackingOptions());

        var result = tracker.Track(target, viewport, TimeSpan.FromMilliseconds(100));

        Assert.False(result.Accepted);
        Assert.Contains("rotation", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Same(initial, tracker.CurrentTransform);
    }

    [Fact]
    public void TracksSequentialCompositionAcrossAcceptedKeyframes()
    {
        using var source = CreateFeatureRichFrame();
        using var first = Warp(source, 1.004, 6, -3);
        using var second = Warp(first, 0.997, -4, 5);
        var viewport = new MapScreenRect(40, 70, source.Width, source.Height);
        var initial = CreateTransform(0.9, 100, 120);
        using var tracker = new MapOrbTracker(source, viewport, initial, new MapOrbTrackingOptions());

        var firstResult = tracker.Track(first, viewport, TimeSpan.FromMilliseconds(100));
        var secondResult = tracker.Track(second, viewport, TimeSpan.FromMilliseconds(100));

        Assert.True(firstResult.Accepted, firstResult.RejectionReason);
        Assert.True(secondResult.Accepted, secondResult.RejectionReason);
        var expectedFirst = MapOrbTracker.Compose(initial, viewport, 1.004, 6, -3);
        var expectedSecond = MapOrbTracker.Compose(expectedFirst, viewport, 0.997, -4, 5);
        Assert.InRange(Math.Abs(secondResult.Transform.OffsetX - expectedSecond.OffsetX), 0, 1.2);
        Assert.InRange(Math.Abs(secondResult.Transform.OffsetY - expectedSecond.OffsetY), 0, 1.2);
        Assert.InRange(Math.Abs((secondResult.Transform.ScaleX / expectedSecond.ScaleX) - 1), 0, 0.003);
    }

    [Fact]
    public void IgnoresDynamicIconsAndPartialOcclusion()
    {
        using var source = CreateFeatureRichFrame();
        using var target = Warp(source, 1, 9, -6);
        Cv2.Rectangle(target, new Rect(0, 0, 150, target.Height), Scalar.Black, -1);
        for (var index = 0; index < 24; index++)
        {
            Cv2.Circle(
                target,
                new Point(190 + (index * 17) % 390, 30 + (index * 31) % 410),
                7,
                index % 2 == 0 ? Scalar.Red : Scalar.Lime,
                -1);
        }
        var viewport = new MapScreenRect(0, 0, source.Width, source.Height);
        var initial = CreateTransform(1, 0, 0);
        using var tracker = new MapOrbTracker(source, viewport, initial, new MapOrbTrackingOptions());

        var result = tracker.Track(target, viewport, TimeSpan.FromMilliseconds(100));

        Assert.True(result.Accepted, result.RejectionReason);
        Assert.InRange(Math.Abs(result.Transform.OffsetX - 9), 0, 1.2);
        Assert.InRange(Math.Abs(result.Transform.OffsetY + 6), 0, 1.2);
    }

    [Theory]
    [InlineData(1.03, 0, 0, "scale")]
    [InlineData(1.00, 100, 0, "translation")]
    public void RejectsUnsafeSingleFrameMotion(
        double scale,
        double dx,
        double dy,
        string expectedReason)
    {
        using var source = CreateFeatureRichFrame();
        using var target = Warp(source, scale, dx, dy);
        var viewport = new MapScreenRect(0, 0, source.Width, source.Height);
        var initial = CreateTransform(1, 0, 0);
        using var tracker = new MapOrbTracker(source, viewport, initial, new MapOrbTrackingOptions());

        var result = tracker.Track(target, viewport, TimeSpan.FromMilliseconds(100));

        Assert.False(result.Accepted);
        Assert.Contains(expectedReason, result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Same(initial, tracker.CurrentTransform);
    }

    [Fact]
    public void ComposeConvertsViewportLocalMotionToAbsoluteScreenCoordinates()
    {
        var transform = CreateTransform(0.75, 420, 180);
        var viewport = new MapScreenRect(300, 100, 800, 600);

        var result = MapOrbTracker.Compose(transform, viewport, 1.01, 6, -3);

        Assert.Equal(0.7575, result.ScaleX, 8);
        Assert.Equal(427.2, result.OffsetX, 8);
        Assert.Equal(177.8, result.OffsetY, 8);
        Assert.Equal(0, result.OrientationDegrees);
    }

    [Fact]
    public void AllResolutionPresetsKeepExperimentalTrackingDisabled()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(
            Path.Combine(root, "Infrastructure", "Configuration", "Presets"),
            "alignment.toml",
            SearchOption.AllDirectories);

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.Contains("[orb_tracking]", text, StringComparison.Ordinal);
            var section = text[(text.IndexOf("[orb_tracking]", StringComparison.Ordinal))..];
            Assert.Contains("enabled = false", section, StringComparison.Ordinal);
        }
    }

    private static Mat CreateFeatureRichFrame()
    {
        var image = Mat.Zeros(480, 640, MatType.CV_8UC3).ToMat();
        var random = new Random(73421);
        for (var index = 0; index < 180; index++)
        {
            var x = random.Next(30, image.Width - 30);
            var y = random.Next(30, image.Height - 30);
            var radius = random.Next(3, 11);
            var shade = random.Next(90, 256);
            Cv2.Circle(image, new Point(x, y), radius, new Scalar(shade, shade, shade), 1 + index % 3);
            Cv2.Line(
                image,
                new Point(x - radius, y - radius),
                new Point(x + radius, y + radius),
                new Scalar(255 - shade / 2d),
                1);
        }
        Cv2.PutText(image, "IDVB ORB TRACKING 0123456789", new Point(55, 240),
            HersheyFonts.HersheyDuplex, 1.1, Scalar.White, 2);
        return image;
    }

    private static Mat Warp(Mat source, double scale, double dx, double dy)
    {
        using var matrix = Mat.FromArray(new[,]
        {
            { scale, 0d, dx },
            { 0d, scale, dy }
        });
        var target = new Mat();
        Cv2.WarpAffine(source, target, matrix, source.Size(), InterpolationFlags.Linear,
            BorderTypes.Constant, Scalar.Black);
        return target;
    }

    private static Mat Rotate(Mat source, double degrees)
    {
        using var matrix = Cv2.GetRotationMatrix2D(
            new Point2f(source.Width / 2f, source.Height / 2f),
            degrees,
            1);
        var target = new Mat();
        Cv2.WarpAffine(source, target, matrix, source.Size(), InterpolationFlags.Linear,
            BorderTypes.Constant, Scalar.Black);
        return target;
    }

    private static MapOverlayTransform CreateTransform(
        double scale,
        double offsetX,
        double offsetY) => new()
    {
        ScaleX = scale,
        ScaleY = scale,
        OffsetX = offsetX,
        OffsetY = offsetY,
        ReferenceCenterX = 320,
        ReferenceCenterY = 240,
        ScreenCenterX = offsetX + 320 * scale,
        ScreenCenterY = offsetY + 240 * scale,
        ReferenceWidth = 640,
        ReferenceHeight = 480,
        OrientationDegrees = 0,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
