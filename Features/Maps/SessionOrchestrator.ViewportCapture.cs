using System.Diagnostics;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task<CapturedGameFrame?> CaptureStableViewportAsync(
        string operation,
        CancellationToken cancellationToken = default,
        IDVBuff.Survey.Contracts.SurveyCaptureTuning? surveyTuning = null)
    {
        var sessionTuning = _settings!.SessionTuning;
        if (_captureSvc.TryGetForegroundClientBounds(
                out var presetBounds,
                out _,
                out _)
            && presetBounds is MapScreenRect validPresetBounds
            && validPresetBounds.IsValid)
        {
            await ApplySelectedResolutionPresetAsync(validPresetBounds);
        }
        var viewport = ResolveMapViewportForCurrentWindow();
        var interval = Math.Max(
            20,
            surveyTuning?.StableFrameDelayMilliseconds
                ?? sessionTuning.StableFrameIntervalMilliseconds);
        var requiredFrames = Math.Max(2, sessionTuning.StableFrameCount);
        var timeout = surveyTuning is null
            ? Math.Max(
                sessionTuning.OpeningTimeoutMilliseconds,
                interval * (requiredFrames + 2))
            : Math.Max(
                surveyTuning.MaximumCaptureMilliseconds,
                interval * (requiredFrames + 2));
        using var tracker = new MapViewportStabilityTracker();
        var stopwatch = Stopwatch.StartNew();
        CapturedGameFrame? lastFrame = null;
        var attempts = 0;
        var successfulCaptures = 0;
        _lastStableCaptureFailureReason = null;

        try
        {
            while (!_disposed
                && !cancellationToken.IsCancellationRequested
                && stopwatch.ElapsedMilliseconds <= timeout)
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
                try
                {
                    await Task.Delay(interval, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _lastStableCaptureFailureReason =
                    "地图对齐已超过时间预算，请保持完整地图打开后重试。";
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
            return ResolveViewportRegion(
                (int)Math.Round(physicalBounds.Width),
                (int)Math.Round(physicalBounds.Height));
        }

        return ResolveViewportRegion(0, 0);
    }

    /// <summary>
    /// 解析地图视口区域：活跃预设的 viewport.toml 优先，
    /// 缺失则回退 settings.json 的按分辨率 / 全局校准，最后整帧。
    /// </summary>
    private NormalizedRectangle ResolveViewportRegion(int width, int height)
    {
        var toml = _config.Get<ViewportCalibrationConfig>("viewport");
        if (toml.ClientWidth == width
            && toml.ClientHeight == height
            && toml.MapRegionWidth >= 0.01
            && toml.MapRegionHeight >= 0.01)
        {
            return new NormalizedRectangle
            {
                X = toml.MapRegionX,
                Y = toml.MapRegionY,
                Width = toml.MapRegionWidth,
                Height = toml.MapRegionHeight
            };
        }

        return _settings!.ResolveMapViewportRegion(width, height)
            ?? new NormalizedRectangle { X = 0, Y = 0, Width = 1, Height = 1 };
    }
}
