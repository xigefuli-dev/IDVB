using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private Task _nativeMiniMapCaptureTask = Task.CompletedTask;
    private sealed record NativeHeadingUpdate(double Degrees, long Timestamp);

    public async Task SetNativeMiniMapRegionAsync(
        NormalizedRectangle region, int clientWidth, int clientHeight, uint observedDpi = 0)
    {
        _settings!.UpsertNativeMiniMapCalibration(region, clientWidth, clientHeight, observedDpi);
        await SaveSettingsAsync();
        _logCollector.Append(MapLogCategory.ViewportCapture, MapLogLevel.Info,
            $"native_minimap calibrated · client={clientWidth}x{clientHeight} · dpi={observedDpi}"
            + $" · roi={region.X:F5},{region.Y:F5},{region.Width:F5},{region.Height:F5} · heading=enabled · screenshotInterval=5s");
    }

    private async Task RunNativeMiniMapCaptureAsync(CancellationToken cancellationToken)
    {
        NativeHeadingUpdate? pending = null;
        var queued = 0;
        double? smoothed = null;
        double? snapped = null;
        long lastGood = 0, lastLog = 0, lastScreenshot = 0, lastFrameTicks = 0;
        string? geometry = null;
        NativeMiniMapHeadingDetector? detector = null;
        void Report(string message, MapLogLevel level = MapLogLevel.Info)
        {
            if (Stopwatch.GetElapsedTime(lastLog).TotalSeconds < 2) return;
            lastLog = Stopwatch.GetTimestamp();
            LogNativeMiniMap(message, level);
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (_settings?.PersistentMiniMapEnabled is not true || _settings.IsEnabled is not true
                        || _gameMapToggleState.IsOpen)
                    {
                        Report("paused · reason=map_open_or_disabled");
                        continue;
                    }
                    if (_activeScanOperations > 0)
                    {
                        Report("paused · reason=scan_active");
                        continue;
                    }
                    if (!Lifecycle.MainProgramPreferences.Load().EnhancedMiniMapEnabled)
                    {
                        Report("paused · reason=enhanced_minimap_disabled");
                        continue;
                    }
                    var watch = Stopwatch.StartNew();
                    if (!_captureSvc.TryGetForegroundClientBounds(out var boundsObject, out var window, out var reason)
                        || boundsObject is not MapScreenRect bounds)
                    {
                        Report($"skipped · reason={reason}");
                        continue;
                    }
                    var region = _settings.ResolveNativeMiniMapRegion(
                        (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), fallbackToDefault: true);
                    if (region is null)
                    {
                        Report($"skipped · reason=uncalibrated · client={bounds.Width}x{bounds.Height}");
                        continue;
                    }
                    var key = $"{window}:{bounds.Width}:{bounds.Height}:{region.X:R}:{region.Y:R}:{region.Width:R}:{region.Height:R}";
                    if (geometry != key)
                    {
                        detector?.Dispose();
                        detector = new NativeMiniMapHeadingDetector();
                        geometry = key;
                        smoothed = null;
                        snapped = null;
                        lastFrameTicks = 0;
                    }
                    // Map-open registration owns WGC while the full map is open; don't compete for its latest frame.
                    var frameObject = await _captureSvc.CaptureNextViewportAsync(region, lastFrameTicks,
                        TimeSpan.FromMilliseconds(35), cancellationToken).ConfigureAwait(false);
                    var backend = "wgc";
                    if (frameObject is null)
                    {
                        backend = "gdi";
                        if (!_captureSvc.TryCaptureViewport(region, out frameObject, out reason))
                        {
                            Report($"capture_failed · reason={reason}", MapLogLevel.Warning);
                            continue;
                        }
                    }
                    if (frameObject is not CapturedGameFrame frame) continue;
                    using (frame)
                    {
                        lastFrameTicks = Math.Max(lastFrameTicks, frame.CaptureSystemRelativeTicks);
                        if (frame.WindowHandle != window || frame.ClientBounds != bounds || _gameMapToggleState.IsOpen)
                        {
                            Report("skipped · reason=window_geometry_or_map_state_changed");
                            continue;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        var captureMs = watch.Elapsed.TotalMilliseconds;
                        var result = detector!.Detect(frame.Image, out reason);
                        var detectMs = watch.Elapsed.TotalMilliseconds - captureMs;
                        if (result is not null)
                        {
                            var delta = smoothed is { } previous
                                ? NativeMiniMapHeadingDetector.ShortestDelta(previous, result.Degrees) : 0;
                            if (smoothed is null || Stopwatch.GetElapsedTime(lastGood).TotalSeconds > .3)
                            {
                                smoothed = result.Degrees;
                            }
                            else
                            {
                                var absDelta = Math.Abs(delta);
                                if (absDelta >= 1.0)
                                {
                                    var factor = absDelta switch
                                    {
                                        < 6.0 => 0.30,
                                        <= 45.0 => 0.75,
                                        _ => Math.Min(1.0, 30.0 / absDelta)
                                    };
                                    smoothed = (smoothed.Value + delta * factor + 360) % 360;
                                }
                            }
                            snapped = NativeMiniMapHeadingDetector.SnapTo8Directions(smoothed.Value, snapped);
                            lastGood = Stopwatch.GetTimestamp();
                            Interlocked.Exchange(ref pending, new NativeHeadingUpdate(snapped.Value, lastGood));
                            if (Interlocked.CompareExchange(ref queued, 1, 0) == 0
                                && !_dispatcher.TryEnqueue(() =>
                                {
                                    try
                                    {
                                        var update = Interlocked.Exchange(ref pending, null);
                                        if (update is null || cancellationToken.IsCancellationRequested || _disposed
                                            || _gameMapToggleState.IsOpen || _settings?.PersistentMiniMapEnabled is not true
                                            || Stopwatch.GetElapsedTime(update.Timestamp).TotalMilliseconds > 150) return;
                                        if (!_captureSvc.TryGetForegroundClientBounds(out _, out var foreground, out _)
                                            || foreground != window) return;
                                        var render = Stopwatch.StartNew();
                                        _overlay.SetNativeMiniMapHeading(update.Degrees);
                                        if (render.Elapsed.TotalMilliseconds > 33)
                                            Report($"slow_render · renderMs={render.Elapsed.TotalMilliseconds:F1}", MapLogLevel.Warning);
                                    }
                                    catch (Exception exception)
                                    {
                                        Report($"render_failed · {exception.Message}", MapLogLevel.Warning);
                                    }
                                    finally { Volatile.Write(ref queued, 0); }
                                })) Volatile.Write(ref queued, 0);
                        }
                        Report($"heading · reason={reason} · slot={result?.Slot} · angle={result?.Degrees:F1}"
                            + $" · displayed={smoothed:F1} · snapped={snapped:F0} · confidence={result?.Confidence:F3} · coneContrast={result?.ConeContrast:F1}"
                            + $" · backend={backend} · captureMs={captureMs:F1} · detectMs={detectMs:F1}"
                            + $" · staleMs={(lastGood == 0 ? -1 : Stopwatch.GetElapsedTime(lastGood).TotalMilliseconds):F0}");
                        if (_settings.DiagnosticModeEnabled && MapDiagnosticModeCapture.IsActive
                            && Stopwatch.GetElapsedTime(lastScreenshot).TotalSeconds >= 5)
                        {
                            lastScreenshot = Stopwatch.GetTimestamp();
                            var path = MapDiagnosticModeCapture.WriteNativeMiniMap(frame.Image);
                            if (path is not null)
                            {
                                using var annotated = frame.Image.Clone();
                                if (result is not null)
                                {
                                    var a = (result.Degrees - 90) * Math.PI / 180;
                                    var center = new Point((int)Math.Round(result.Center.X), (int)Math.Round(result.Center.Y));
                                    Cv2.ArrowedLine(annotated, center,
                                        new Point(center.X + (int)(Math.Cos(a) * 45), center.Y + (int)(Math.Sin(a) * 45)),
                                        new Scalar(0, 255, 0), 1);
                                }
                                Cv2.PutText(annotated, result is null ? reason : $"{result.Slot} {result.Degrees:F1}",
                                    new Point(2, 12), HersheyFonts.HersheySimplex, .3, new Scalar(0, 255, 0));
                                MapDiagnosticModeCapture.WriteNativeMiniMap(annotated, "_heading");
                                LogNativeMiniMap($"saved · path={path} · reason={reason} · angle={result?.Degrees:F1}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    Report($"failed · {exception.GetType().Name}: {exception.Message}", MapLogLevel.Warning);
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { detector?.Dispose(); }
    }

    private void LogNativeMiniMap(string message, MapLogLevel level = MapLogLevel.Info) =>
        _logCollector.Append(MapLogCategory.ViewportCapture, level, $"native_minimap {message}");
}
