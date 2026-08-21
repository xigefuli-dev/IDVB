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
        Func<bool>? shouldContinue = null)
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
            return await CaptureReadyViewportForLockedMapAsync(
                operation,
                viewport,
                interval,
                cancellationToken,
                shouldContinue);
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

    /// <summary>
    /// 已锁定地图重新开图（仅对齐）专用：不做连续 N 帧稳定等待，改为逐帧用
    /// MapViewportPresenceDetector 相似度判断画面就绪——只要与已记录参考高度
    /// 相似（或 blue-gray 高占比）就直接进入对齐，省去开图动画帧的稳定等待。
    /// 后续结构验证 / 缩放缓存验证兜底，错误帧不会被提交。
    /// </summary>
    private async Task<CapturedGameFrame?> CaptureReadyViewportForLockedMapAsync(
        string operation,
        NormalizedRectangle viewport,
        int interval,
        CancellationToken cancellationToken,
        Func<bool>? shouldContinue)
    {
        var sessionTuning = _settings!.SessionTuning;
        // 就绪等待没有 requiredFrames 概念：超时只保留硬性兜底。
        // Readiness is not a performance timeout. Keep polling until the
        // caller cancels because the match ended or a newer operation took
        // ownership; a late game frame must still be aligned honestly.
        var timeout = double.PositiveInfinity;
        var stopwatch = Stopwatch.StartNew();
        CapturedGameFrame? lastFrame = null;
        var attempts = 0;
        var successfulCaptures = 0;
        var presenceRejections = 0;
        MapViewportPresenceResult? lastPresence = null;
        // blue-gray 兜底（无参考签名）对等比例调暗天然不敏感：跟踪上一帧的颜色
        // 签名，要求连续两帧明度一致才放行，避免淡入类动画第一帧就误判就绪。
        MapViewportColorSignature? previousSignature = null;
        // 轮询成本会直接变成就绪判定的过冲：单次尝试越贵，检测到"已就绪"的
        // 时刻越晚。分开记账，便于用日志校准而不是靠猜。
        var captureMilliseconds = 0d;
        var signatureMilliseconds = 0d;
        _lastStableCaptureFailureReason = null;

        try
        {
            while (!_disposed
                && !cancellationToken.IsCancellationRequested
                && (shouldContinue?.Invoke() ?? true)
                && stopwatch.ElapsedMilliseconds <= timeout)
            {
                attempts++;
                var captureTimer = Stopwatch.StartNew();
                var captureSpan = ActiveOperationTrace?.StartChild(
                    "capture",
                    MapOperationWaitKind.Capture,
                    attemptIndex: attempts);
                bool captured;
                object? frameObj;
                string failureReason;
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
                captureTimer.Stop();
                if (captured && frameObj is CapturedGameFrame current)
                {
                    successfulCaptures++;
                    DisposeViewportFrame(lastFrame, attempts);
                    lastFrame = current;
                    // 本帧签名同时用于就绪判定和下一帧的明度基线，只能算一次。
                    var signatureTimer = Stopwatch.StartNew();
                    var signatureSpan = ActiveOperationTrace?.StartChild(
                        "signature",
                        MapOperationWaitKind.Compute,
                        attemptIndex: attempts);
                    MapViewportColorSignature signature;
                    try
                    {
                        signature = MapViewportPresenceDetector.CreateSignature(
                            current.Image);
                    }
                    finally
                    {
                        signatureSpan?.Complete();
                    }
                    signatureTimer.Stop();
                    lastPresence = MapViewportPresenceDetector.EvaluateReady(
                        signature,
                        GetCurrentMapViewportPresenceReference(),
                        previousSignature);
                    previousSignature = signature;
                    captureMilliseconds += captureTimer.Elapsed.TotalMilliseconds;
                    signatureMilliseconds +=
                        signatureTimer.Elapsed.TotalMilliseconds;
                    if (lastPresence.IsPresent)
                    {
                        _logCollector.Append(
                            MapLogCategory.ViewportCapture,
                            MapLogLevel.Info,
                            $"画面就绪确认完成 · op={operation} · attempts={attempts} "
                            + $"· captures={successfulCaptures} · mode={lastPresence.Mode} "
                            + $"· score={lastPresence.Score:F4}",
                            elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                            details: new()
                            {
                                ["operation"] = operation,
                                ["attempts"] = attempts,
                                ["successfulCaptures"] = successfulCaptures,
                                ["mapPresenceMode"] = lastPresence.Mode,
                                ["mapPresenceScore"] = lastPresence.Score,
                                ["blueGrayFraction"] = lastPresence.BlueGrayFraction,
                                ["presenceRejections"] = presenceRejections,
                                ["readyWaitMs"] = stopwatch.Elapsed.TotalMilliseconds,
                                ["captureMs"] = captureMilliseconds,
                                ["signatureMs"] = signatureMilliseconds,
                                ["pollIntervalMs"] = interval,
                                ["perAttemptMs"] = attempts > 0
                                    ? stopwatch.Elapsed.TotalMilliseconds / attempts
                                    : 0d
                            });
                        var readyFrame = lastFrame;
                        lastFrame = null;
                        return readyFrame;
                    }

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
                    // 逐帧记录被拒帧 score——这是校准就绪阈值的动画帧实证数据。
                    _logCollector.Append(
                        MapLogCategory.ViewportCapture,
                        MapLogLevel.Info,
                        $"画面未就绪 · op={operation} · mode={lastPresence.Mode} "
                        + $"· score={lastPresence.Score:F4} "
                        + $"· blueGray={lastPresence.BlueGrayFraction:F4}",
                        elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                        details: new()
                        {
                            ["operation"] = operation,
                            ["attempts"] = attempts,
                            ["captureMs"] = captureTimer.Elapsed.TotalMilliseconds,
                            ["signatureMs"] = signatureMilliseconds,
                            ["mapPresenceScore"] = lastPresence.Score
                        });
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
                    "地图对齐已取消，请保持完整地图打开后重试。";
            }
            else if (!(shouldContinue?.Invoke() ?? true))
            {
                _lastStableCaptureFailureReason =
                    "地图对齐已被新的地图开关操作取消。";
            }

            _logCollector.Append(
                MapLogCategory.ViewportCapture,
                MapLogLevel.Warning,
                $"画面就绪确认超时 · op={operation} · attempts={attempts} "
                + $"· captures={successfulCaptures} · lastMode={lastPresence?.Mode} "
                + $"· lastScore={lastPresence?.Score:F4}",
                elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                details: new()
                {
                    ["operation"] = operation,
                    ["attempts"] = attempts,
                    ["successfulCaptures"] = successfulCaptures,
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

    private static void DisposeViewportFrame(IDisposable? frame, int attemptIndex)
    {
        if (frame is null)
            return;

        var dispose = MapOperationTraceAmbient.StartChild(
            "frame_dispose",
            MapOperationWaitKind.Io,
            attemptIndex: attemptIndex);
        try
        {
            frame.Dispose();
        }
        finally
        {
            dispose.Complete();
        }
    }

    private MapViewportColorSignature? GetCurrentMapViewportPresenceReference()
    {
        var identity = _lastRecognition ?? _pendingAlignmentIdentity;
        if (identity is null)
            return null;
        var floorKey = _currentFloorKey
            ?? identity.Result.Floor
            ?? MapFloorRules.GetPrimaryFloorKey(identity.Map);
        var key = new MapViewportReferenceKey(
            identity.Map.Id,
            NormalizeMapViewportFloorKey(floorKey));
        lock (_mapViewportReferenceGate)
        {
            return _mapViewportReferences.GetValueOrDefault(key);
        }
    }

    private void RememberMapViewportPresenceReference(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        var key = new MapViewportReferenceKey(
            recognition.Map.Id,
            NormalizeMapViewportFloorKey(recognition.Result.Floor));
        var signature = MapViewportPresenceDetector.CreateSignature(frame.Image);
        lock (_mapViewportReferenceGate)
        {
            _mapViewportReferences[key] = signature;
        }
    }

    private void ClearMapViewportPresenceReferences()
    {
        lock (_mapViewportReferenceGate)
        {
            _mapViewportReferences.Clear();
        }
    }

    private static string NormalizeMapViewportFloorKey(string? floorKey) =>
        string.IsNullOrWhiteSpace(floorKey)
            ? "1f"
            : floorKey.Trim().ToLowerInvariant();

    private readonly record struct MapViewportReferenceKey(
        Guid MapId,
        string FloorKey);

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
/*
 * 文件职责：SessionOrchestrator.ViewportCapture。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
