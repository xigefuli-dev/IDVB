using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit.Abstractions;
using System.Diagnostics;

namespace IDVBuff.Tests;

public sealed class ViewportDynamicMaskTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Frames()
    {
        foreach (var channels in new[] { 3, 4 })
            foreach (var size in new[] { new Size(79, 51), new Size(321, 203), new Size(2560, 1600) })
                yield return new object[] { channels, size.Width, size.Height, "" };

        // Optional local replay inputs are never copied into the repository.
        var directory = Environment.GetEnvironmentVariable("IDVB_VIEWPORT_REPLAY_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(directory))
            foreach (var path in Directory.GetFiles(directory, "*.png"))
                yield return new object[] { 0, 0, 0, path };
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void SampledMaskEqualsFullResolutionReference(int channels, int width, int height, string path)
    {
        using var source = string.IsNullOrEmpty(path)
            ? new Mat(height, width, MatType.CV_8UC(channels))
            : Cv2.ImRead(path, ImreadModes.Unchanged);
        Assert.False(source.Empty());
        if (string.IsNullOrEmpty(path))
        {
            var pixels = new byte[width * height * channels];
            new Random(431).NextBytes(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, source.Data, pixels.Length);
        }
        using var expected = new Mat(100, 160, MatType.CV_8UC1, Scalar.White);
        using var actual = expected.Clone();
        var started = Stopwatch.GetTimestamp();
        FullResolutionMask(source, expected);
        var before = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        MapViewportStabilityTracker.MaskSaturatedDynamicPixels(source, actual);
        var after = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(0d, Cv2.Norm(expected, actual, NormTypes.INF));
        output.WriteLine($"{source.Width}x{source.Height}: old={before:F3}ms new={after:F3}ms maskDiff=0");
    }

    // Preserve the original full-resolution algorithm as an independent oracle.
    private static void FullResolutionMask(Mat source, Mat target)
    {
        if (source.Channels() < 3) return;
        using var bgr = new Mat();
        if (source.Channels() == 4) Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        else source.CopyTo(bgr);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        try
        {
            using var saturated = new Mat();
            using var bright = new Mat();
            using var mask = new Mat();
            using var resized = new Mat();
            Cv2.Threshold(channels[1], saturated, 105, 255, ThresholdTypes.Binary);
            Cv2.Threshold(channels[2], bright, 70, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(saturated, bright, mask);
            Cv2.Resize(mask, resized, target.Size(), interpolation: InterpolationFlags.Nearest);
            target.SetTo(Scalar.Black, resized);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }
}
