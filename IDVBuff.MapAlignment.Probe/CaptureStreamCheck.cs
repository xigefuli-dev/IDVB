using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using IDVBuff.Features.Maps;

namespace IDVBuff.MapAlignment.Probe;

/// <summary>Explicit native capture check; never activates the supplied window or writes its pixels.</summary>
internal static class CaptureStreamCheck
{
    public static async Task<int> RunAsync(nint window, string[] region)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, 9, out var rect, 16));
        var bounds = new MapScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var viewport = region.Length == 4
            ? DwrGameWindowCaptureService.GetViewportBounds(bounds, new NormalizedRectangle
            {
                X = double.Parse(region[0], System.Globalization.CultureInfo.InvariantCulture),
                Y = double.Parse(region[1], System.Globalization.CultureInfo.InvariantCulture),
                Width = double.Parse(region[2], System.Globalization.CultureInfo.InvariantCulture),
                Height = double.Parse(region[3], System.Globalization.CultureInfo.InvariantCulture)
            }) : bounds;
        var start = Stopwatch.GetTimestamp();
        using var stream = new GameFrameStream(window);
        var prepareMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var after = (long)(start * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        for (var index = 0; index < 12; index++)
        {
            start = Stopwatch.GetTimestamp();
            using var frame = await stream.CaptureAsync(bounds, viewport, after, deadline.Token)
                ?? throw new InvalidOperationException("Native frame geometry rejected.");
            if (frame.CaptureSystemRelativeTicks <= after || frame.Image.Width != (int)viewport.Width
                || frame.Image.Height != (int)viewport.Height || frame.Image.Channels() != 4)
                throw new InvalidOperationException("Native frame freshness/geometry check failed.");
            after = frame.CaptureSystemRelativeTicks;
            var receiveMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var ageAtReceiveMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency
                - after / (double)TimeSpan.TicksPerMillisecond;
            var signature = MapViewportPresenceDetector.CreateSignature(frame.Image);
            var observation = frame.GetOrCreateVpsg3Observation();
            if (!ReferenceEquals(observation, frame.GetOrCreateVpsg3Observation()))
                throw new InvalidOperationException("Prepared contour was recomputed.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                index, prepareMs, receiveMs, ageAtReceiveMs, viewport.Width, viewport.Height,
                frameToContourMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency
                    - after / (double)TimeSpan.TicksPerMillisecond,
                contourMs = observation.ExtractionMilliseconds,
                observation.EdgePixelCount, signature.BlueGrayFraction
            }));
        }
        using (var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(25)))
        {
            try
            {
                await stream.CaptureAsync(bounds, viewport, long.MaxValue, canceled.Token);
                throw new InvalidOperationException("Canceled frame wait returned normally.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
        stream.Dispose();
        if (await stream.CaptureAsync(bounds, bounds, after, CancellationToken.None) is not null)
            throw new InvalidOperationException("Disposed stream returned a frame.");
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
}
