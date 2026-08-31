using System.Diagnostics;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

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
        Func<bool>? shouldContinue,
        bool requireStructureReadiness,
        int structureFallbackFrameCount)
    {
        var sessionTuning = _settings!.SessionTuning;
        // Readiness must remain bounded. A stale floor reference or a map that
        // was closed before settling must not keep the input handler alive
        // until another toggle happens to cancel it.
        var timeout = Math.Max(
            sessionTuning.OpeningTimeoutMilliseconds,
            interval * 4d);
        var stopwatch = Stopwatch.StartNew();
        CapturedGameFrame? lastFrame = null;
        var attempts = 0;
        var successfulCaptures = 0;
        var presenceRejections = 0;
        MapViewportPresenceResult? lastPresence = null;
        // blue-gray 兜底（无参考签名）对等比例调暗天然不敏感：跟踪上一帧的颜色
        // 签名，要求连续两帧明度一致才放行，避免淡入类动画第一帧就误判就绪。
        MapViewportColorSignature? previousSignature = null;
        var stableStructureFrames = 0;
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
                    var presenceReference =
                        GetCurrentMapViewportPresenceReference();
                    double? referenceStructureSimilarity = null;
                    double? consecutiveStructureSimilarity = null;
                    lastPresence = MapViewportPresenceDetector.EvaluateReady(
                        signature,
                        presenceReference,
                        previousSignature,
                        requireStructure: requireStructureReadiness,
                        requiredStableStructureFrames:
                            Math.Clamp(structureFallbackFrameCount, 2, 5),
                        observedStableStructureFrames: stableStructureFrames);
                    if (requireStructureReadiness)
                    {
                        // This counter measures whether the opening animation
                        // has settled, so compare consecutive live frames. The
                        // saved reference belongs to an earlier alignment and
                        // may legitimately have a different translation; using
                        // it here permanently pins the counter at one after the
                        // map recenters.
                        if (signature.Structure is { } currentStructure
                            && presenceReference?.Structure is { } referenceStructure)
                        {
                            referenceStructureSimilarity =
                                MapViewportPresenceDetector.StructureSimilarity(
                                    currentStructure,
                                    referenceStructure);
                        }
                        if (signature.Structure is { } liveStructure
                            && previousSignature?.Structure is { } previousStructure)
                        {
                            consecutiveStructureSimilarity =
                                MapViewportPresenceDetector.StructureSimilarity(
                                    liveStructure,
                                    previousStructure);
                        }
                        var structureIsConsistent =
                            consecutiveStructureSimilarity >= 0.90d;
                        stableStructureFrames = structureIsConsistent
                            ? stableStructureFrames + 1
                            : 1;
                        lastPresence = MapViewportPresenceDetector.EvaluateReady(
                            signature,
                            presenceReference,
                            previousSignature,
                            requireStructure: true,
                            requiredStableStructureFrames:
                                Math.Clamp(structureFallbackFrameCount, 2, 5),
                            observedStableStructureFrames: stableStructureFrames);
                    }
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
                                ["referenceStructureSimilarity"] =
                                    referenceStructureSimilarity,
                                ["consecutiveStructureSimilarity"] =
                                    consecutiveStructureSimilarity,
                                ["stableStructureFrames"] = stableStructureFrames,
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
                            ["mapPresenceScore"] = lastPresence.Score,
                            ["referenceStructureSimilarity"] =
                                referenceStructureSimilarity,
                            ["consecutiveStructureSimilarity"] =
                                consecutiveStructureSimilarity,
                            ["stableStructureFrames"] = stableStructureFrames
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
