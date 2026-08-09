using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task<CapturedGameFrame?> CaptureStableViewportAsync(
        string operation)
    {
        var sessionTuning = _settings!.SessionTuning;
        var viewport = ResolveMapViewportForCurrentWindow();
        var interval = Math.Max(20, sessionTuning.StableFrameIntervalMilliseconds);
        var requiredFrames = Math.Max(2, sessionTuning.StableFrameCount);
        var timeout = Math.Max(
            sessionTuning.OpeningTimeoutMilliseconds,
            interval * (requiredFrames + 2));
        using var tracker = new MapViewportStabilityTracker();
        var stopwatch = Stopwatch.StartNew();
        CapturedGameFrame? lastFrame = null;
        var attempts = 0;
        var successfulCaptures = 0;
        _lastStableCaptureFailureReason = null;

        try
        {
            while (!_disposed && stopwatch.ElapsedMilliseconds <= timeout)
            {
                attempts++;
                if (_captureSvc.TryCaptureViewport(
                        viewport,
                        out var frameObj,
                        out var failureReason)
                    && frameObj is CapturedGameFrame current)
                {
                    successfulCaptures++;
                    var stable = tracker.Observe(
                        current.Image,
                        sessionTuning.StableFrameDifference,
                        requiredFrames,
                        sessionTuning.ViewportIgnoreRegions);
                    lastFrame?.Dispose();
                    lastFrame = current;
                    if (stable)
                    {
                        _logCollector.Append(
                            MapLogCategory.ViewportCapture,
                            MapLogLevel.Info,
                            $"稳定帧确认完成 · op={operation} · attempts={attempts} "
                            + $"· captures={successfulCaptures} · diff={tracker.LastDifference:F4}",
                            elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                            details: new()
                            {
                                ["operation"] = operation,
                                ["attempts"] = attempts,
                                ["successfulCaptures"] = successfulCaptures,
                                ["stableFrames"] = tracker.StableFrames,
                                ["lastDifference"] = tracker.LastDifference
                            });
                        var stableFrame = lastFrame;
                        lastFrame = null;
                        return stableFrame;
                    }
                }
                else if (frameObj is IDisposable disposable)
                {
                    disposable.Dispose();
                    _lastStableCaptureFailureReason = string.IsNullOrWhiteSpace(failureReason)
                        ? "地图截图失败。"
                        : failureReason;
                }
                else
                {
                    _lastStableCaptureFailureReason = string.IsNullOrWhiteSpace(failureReason)
                        ? "地图截图失败。"
                        : failureReason;
                }

                if (stopwatch.ElapsedMilliseconds + interval > timeout)
                    break;
                await Task.Delay(interval);
            }

            _logCollector.Append(
                MapLogCategory.ViewportCapture,
                MapLogLevel.Warning,
                $"稳定帧确认超时 · op={operation} · attempts={attempts} "
                + $"· captures={successfulCaptures} · diff={tracker.LastDifference:F4}",
                elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                details: new()
                {
                    ["operation"] = operation,
                    ["attempts"] = attempts,
                    ["successfulCaptures"] = successfulCaptures,
                    ["stableFrames"] = tracker.StableFrames,
                    ["lastDifference"] = tracker.LastDifference,
                    ["captureFailureReason"] = _lastStableCaptureFailureReason
                });
            lastFrame?.Dispose();
            lastFrame = null;
            return null;
        }
        finally
        {
            lastFrame?.Dispose();
        }
    }

    private NormalizedRectangle ResolveMapViewportForCurrentWindow()
    {
        if (_captureSvc.TryGetForegroundClientBounds(
                out var clientBounds,
                out _,
                out _)
            && clientBounds is MapScreenRect physicalBounds)
        {
            var resolved = _settings!.ResolveMapViewportRegion(
                (int)Math.Round(physicalBounds.Width),
                (int)Math.Round(physicalBounds.Height));
            if (resolved?.IsValid is true)
                return resolved;
        }

        return _settings!.MapViewportRegion
            ?? new NormalizedRectangle { X = 0, Y = 0, Width = 1, Height = 1 };
    }
}
