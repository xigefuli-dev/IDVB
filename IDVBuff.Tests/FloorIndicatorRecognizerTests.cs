using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using IDVBuff.Features.Maps;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed class FloorIndicatorRecognizerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("1F.png", "1f")]
    [InlineData("2F.png", "2f")]
    public void ReferenceImagesAreClassifiedCorrectly(
        string fileName,
        string expectedFloor)
    {
        var recognizer = CreateRecognizer();
        var frame = LoadFrame(fileName);

        var result = recognizer.Recognize(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(expectedFloor, result.Floor);
    }

    [Theory]
    [InlineData("1F.png", "1f")]
    [InlineData("2F.png", "2f")]
    public void ScaledDimmedBlurredAndNoisyImagesRemainRecognizable(
        string fileName,
        string expectedFloor)
    {
        var recognizer = CreateRecognizer();
        var frame = LoadFrame(
            fileName,
            scale: 0.7d,
            brightness: 0.72d,
            soften: true,
            noiseAmplitude: 6);

        var result = recognizer.Recognize(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(expectedFloor, result.Floor);
    }

    [Theory]
    [InlineData("1F.png", "1f")]
    [InlineData("2F.png", "2f")]
    public void DigitPairIsLocalizedInsideCoarseCalibrationRegion(
        string fileName,
        string expectedFloor)
    {
        using var source = new Bitmap(AssetPath(fileName));
        using var canvas = new Bitmap(
            672,
            271,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.FromArgb(255, 18, 28, 39));
            graphics.DrawImage(source, 173, 41, source.Width, source.Height);
        }
        var frame = ReadFrame(canvas);
        using var recognizer = CreateRecognizer();

        var result = recognizer.Recognize(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(expectedFloor, result.Floor);
        Assert.True(result.LocalizationConfidence >= 0.40d);
        Assert.True(result.LocalizedRegion?.IsValid == true);
    }

    [Fact]
    public void EqualButtonActivityIsRejected()
    {
        var recognizer = CreateRecognizer();
        const int width = 240;
        const int height = 120;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 70;
            pixels[index + 1] = 78;
            pixels[index + 2] = 84;
            pixels[index + 3] = 255;
        }

        var result = recognizer.Recognize(
            pixels,
            width,
            height,
            width * 4);

        Assert.False(result.Succeeded);
        Assert.Null(result.Floor);
    }

    [Theory]
    [InlineData("1f")]
    [InlineData("2f")]
    public void OutOfProfileTextureIsRejectedInsteadOfGuessing(
        string texturedSide)
    {
        var recognizer = CreateRecognizer();
        var frame = CreateBrightnessTextureConflictFrame(texturedSide);

        var result = recognizer.Recognize(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride);

        Assert.False(result.Succeeded);
        Assert.Null(result.Floor);
    }

    [Fact]
    public void WarmClassifierRunsFarBelowHardDeadline()
    {
        var recognizer = CreateRecognizer();
        var first = LoadFrame("1F.png");
        var second = LoadFrame("2F.png");
        _ = recognizer.Recognize(
            first.Pixels,
            first.Width,
            first.Height,
            first.Stride);

        const int iterations = 100;
        var maximum = 0d;
        var stopwatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var frame = iteration % 2 == 0 ? first : second;
            var result = recognizer.Recognize(
                frame.Pixels,
                frame.Width,
                frame.Height,
                frame.Stride);
            Assert.True(result.Succeeded, result.FailureReason);
            maximum = Math.Max(maximum, result.AnalysisMilliseconds);
        }
        stopwatch.Stop();
        var average = stopwatch.Elapsed.TotalMilliseconds / iterations;
        output.WriteLine(
            $"Floor classifier average {average:F3}ms; maximum {maximum:F3}ms.");

        Assert.True(
            maximum < MapFloorRecognitionRules.PerformanceBudgetMilliseconds,
            $"One classification took {maximum:F1}ms.");
        Assert.True(average < 30d, $"Average classification took {average:F1}ms.");
    }

    private static FloorIndicatorRecognizer CreateRecognizer() =>
        new(
            AssetPath("1F.png"),
            AssetPath("2F.png"));

    private static string AssetPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

    private static PixelFrame LoadFrame(
        string fileName,
        double scale = 1d,
        double brightness = 1d,
        bool soften = false,
        int noiseAmplitude = 0)
    {
        using var source = new Bitmap(AssetPath(fileName));
        var width = Math.Max(16, (int)Math.Round(source.Width * scale));
        var height = Math.Max(16, (int)Math.Round(source.Height * scale));
        using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var processed = soften
            ? Soften(resized)
            : new Bitmap(resized);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var data = processed.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    data.Scan0 + (row * data.Stride),
                    pixels,
                    row * stride,
                    stride);
            }
        }
        finally
        {
            processed.UnlockBits(data);
        }

        var random = new Random(1729);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var noise = noiseAmplitude == 0
                    ? 0
                    : random.Next(-noiseAmplitude, noiseAmplitude + 1);
                pixels[index + channel] = (byte)Math.Clamp(
                    (int)Math.Round(pixels[index + channel] * brightness) + noise,
                    0,
                    255);
            }
        }
        return new PixelFrame(pixels, width, height, stride);
    }

    private static PixelFrame ReadFrame(Bitmap bitmap)
    {
        var stride = bitmap.Width * 4;
        var pixels = new byte[stride * bitmap.Height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(
                    data.Scan0 + (row * data.Stride),
                    pixels,
                    row * stride,
                    stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return new PixelFrame(
            pixels,
            bitmap.Width,
            bitmap.Height,
            stride);
    }

    private static Bitmap Soften(Bitmap source)
    {
        using var small = new Bitmap(
            Math.Max(8, source.Width / 2),
            Math.Max(8, source.Height / 2),
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, small.Width, small.Height);
        }
        var result = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(small, 0, 0, result.Width, result.Height);
        }
        return result;
    }

    private static PixelFrame CreateBrightnessTextureConflictFrame(
        string texturedSide)
    {
        const int width = 240;
        const int height = 120;
        const int stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var isLeft = x < width / 2;
                var isTextured = texturedSide == "1f"
                    ? isLeft
                    : !isLeft;
                // Make the textured side darker, so mean brightness points at
                // the wrong floor while deviation and edges point at the right
                // active button.
                var value = isTextured
                    ? ((x + y) % 2 == 0 ? 45 : 125)
                    : 190;
                var index = (y * stride) + (x * 4);
                pixels[index] = (byte)value;
                pixels[index + 1] = (byte)value;
                pixels[index + 2] = (byte)value;
                pixels[index + 3] = 255;
            }
        }
        return new PixelFrame(pixels, width, height, stride);
    }

    private readonly record struct PixelFrame(
        byte[] Pixels,
        int Width,
        int Height,
        int Stride);
}
