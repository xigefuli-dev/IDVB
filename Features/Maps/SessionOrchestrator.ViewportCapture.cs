using System.Diagnostics;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task<CapturedGameFrame?> CaptureSurveyViewportOnceAsync(
        string operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_captureSvc.TryGetForegroundClientBounds(
                out var presetBounds,
                out _,
                out _)
            && presetBounds is MapScreenRect validPresetBounds
            && validPresetBounds.IsValid)
        {
            await ApplySelectedResolutionPresetAsync(validPresetBounds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _lastStableCaptureFailureReason = null;
        var viewport = ResolveMapViewportForCurrentWindow();
        if (_captureSvc.TryCaptureViewport(
                viewport,
                out var frame,
                out var failureReason)
            && frame is CapturedGameFrame captured)
        {
            return captured;
        }

        _lastStableCaptureFailureReason = string.IsNullOrWhiteSpace(failureReason)
            ? $"{operation} 截图失败。"
            : failureReason;
        return null;
    }

    private async Task<CapturedGameFrame?> CaptureStableViewportAsync(
        string operation,
        CancellationToken cancellationToken = default,
        IDVBuff.Survey.Contracts.SurveyCaptureTuning? surveyTuning = null,
        bool relaxForLockedMap = false,
        Func<bool>? shouldContinue = null,
        bool lowStructureReadiness = false,
        int lowStructureReadinessFrameCount = 3)
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
        var interval = relaxForLockedMap
            ? 8
            : Math.Max(
                10,
                surveyTuning?.StableFrameDelayMilliseconds
                    ?? sessionTuning.StableFrameIntervalMilliseconds);
        // 已锁定地图重新开图（仅对齐）：不再等连续 N 帧 diff 稳定，改为逐帧用
        // 相似度判断画面就绪（与对齐成功的参考签名比对），命中直接进对齐，
        // 省去开图动画帧的稳定等待。后续结构 / 缓存验证兜底错误帧不提交。
        if (relaxForLockedMap)
        {
            var structureFallbackFrameCount = lowStructureReadiness
                ? lowStructureReadinessFrameCount
                : 2;
            return await CaptureReadyViewportForLockedMapAsync(
                operation,
                viewport,
                interval,
                cancellationToken,
                shouldContinue,
                // Standard floors retain the established reference/blue-gray
                // readiness path. Requiring structure here can wait forever
                // before the aligner is entered when the saved reference is
                // stale or unavailable. Only the explicitly low-structure
                // channel needs consecutive structural readiness evidence.
                requireStructureReadiness: lowStructureReadiness,
                structureFallbackFrameCount);
        }

        var requiredFrames = Math.Max(2, sessionTuning.StableFrameCount);
        var maximumDifference = sessionTuning.StableFrameDifference;
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
        var presenceRejections = 0;
        MapViewportPresenceResult? lastPresence = null;
        _lastStableCaptureFailureReason = null;

        try
        {
            while (!_disposed
                && !cancellationToken.IsCancellationRequested
                && stopwatch.ElapsedMilliseconds <= timeout)
            {
                attempts++;
                object? frameObj;
                string failureReason;
                bool captured;
                var captureSpan = ActiveOperationTrace?.StartChild(
                    "capture",
                    MapOperationWaitKind.Capture,
                    attemptIndex: attempts);
                try
                {
                    captured = _captureSvc.TryCaptureViewport(
                        viewport,
                        out frameObj,
                        out failureReason);
                }
                finally
                {
                    captureSpan?.Complete();
                }
                if (captured
                    && frameObj is CapturedGameFrame current)
                {
                    successfulCaptures++;
                    var stable = tracker.Observe(
                        current.Image,
                        maximumDifference,
                        requiredFrames,
                        sessionTuning.ViewportIgnoreRegions);
                    DisposeViewportFrame(lastFrame, attempts);
                    lastFrame = current;
                    if (stable)
                    {
                        var signatureSpan = ActiveOperationTrace?.StartChild(
                            "signature",
                            MapOperationWaitKind.Compute,
                            attemptIndex: attempts);
                        try
                        {
                            lastPresence = MapViewportPresenceDetector.Evaluate(
                                current.Image,
                                GetCurrentMapViewportPresenceReference());
                        }
                        finally
                        {
                            signatureSpan?.Complete();
                        }
                        if (!lastPresence.IsPresent)
                        {
                            presenceRejections++;
                            var rejectionSpan = ActiveOperationTrace?.StartChild(
                                "stability_rejection",
                                MapOperationWaitKind.Compute,
                                attemptIndex: attempts);
                            rejectionSpan?.Complete(
                                MapOperationSpanStatus.Failed,
                                $"presence-{lastPresence.Mode}");
                            _lastStableCaptureFailureReason =
                                "未检测到完整地图画面，请保持地图打开后重试。";
                            if (presenceRejections == 1)
                            {
                                _logCollector.Append(
                                    MapLogCategory.ViewportCapture,
                                    MapLogLevel.Info,
                                    $"稳定画面未通过地图存在检测 · op={operation} "
                                    + $"· mode={lastPresence.Mode} "
                                    + $"· score={lastPresence.Score:F4} "
                                    + $"· blueGray={lastPresence.BlueGrayFraction:F4}",
                                    elapsedMs: stopwatch.Elapsed.TotalMilliseconds);
                            }
                        }
                        else
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
                                    ["lastDifference"] = tracker.LastDifference,
                                    ["mapPresenceMode"] = lastPresence.Mode,
                                    ["mapPresenceScore"] = lastPresence.Score,
                                    ["blueGrayFraction"] = lastPresence.BlueGrayFraction,
                                    ["presenceRejections"] = presenceRejections
                                });
                            var stableFrame = lastFrame;
                            lastFrame = null;
                            return stableFrame;
                        }
                    }
                }
                else if (frameObj is IDisposable disposable)
                {
                    DisposeViewportFrame(disposable, attempts);
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
                    var retryDelay = ActiveOperationTrace?.StartChild(
                        "retry_delay",
                        MapOperationWaitKind.Timer,
                        attemptIndex: attempts);
                    try
                    {
                        await Task.Delay(interval, cancellationToken);
                    }
                    finally
                    {
                        retryDelay?.Complete();
                    }
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
                    ["mapPresenceMode"] = lastPresence?.Mode,
                    ["mapPresenceScore"] = lastPresence?.Score,
                    ["blueGrayFraction"] = lastPresence?.BlueGrayFraction,
                    ["presenceRejections"] = presenceRejections,
                    ["captureFailureReason"] = _lastStableCaptureFailureReason
                });
            DisposeViewportFrame(lastFrame, attempts);
            lastFrame = null;
            return null;
        }
        finally
        {
            DisposeViewportFrame(lastFrame, attempts);
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.ViewportCapture。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
