using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private sealed record MapOpenViewportCaptureResult(
        CapturedGameFrame? Frame,
        double StableViewportWaitMilliseconds,
        string StableViewportMode,
        bool StableViewportFallback,
        MapRecognitionAttempt? PrecomputedVpsg3Attempt);

    private async Task<MapOpenViewportCaptureResult> CaptureMapOpenViewportAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot match,
        RuntimeMapRecognition locked,
        string floorKey,
        bool recoveringSelectedIdentity,
        bool independentAlignment,
        MapStructureRegistrationTuning initialPrewarmTuning,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stableViewportTimer = Stopwatch.StartNew();
        var channel = MapAlignmentChannelRegistry.Resolve(locked.Map, floorKey);
        var canUseSteadyFastCapture = false;
        var viewport = new NormalizedRectangle();
        ReliableFloorAlignmentSeed? warmSeed = null;

        if (!recoveringSelectedIdentity
            && !independentAlignment
            && channel.Channel != MapAlignmentChannel.LowStructure
            && _captureSvc.TryGetForegroundClientBounds(
                out var presetBoundsObject,
                out _,
                out _)
            && presetBoundsObject is MapScreenRect presetBounds
            && presetBounds.IsValid)
        {
            await ApplySelectedResolutionPresetAsync(presetBounds);
            if (_captureSvc.TryGetForegroundClientBounds(
                    out var clientBoundsObject,
                    out _,
                    out _)
                && clientBoundsObject is MapScreenRect clientBounds
                && clientBounds.IsValid)
            {
                viewport = ResolveMapViewportForCurrentWindow();
                var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(
                    clientBounds,
                    viewport);
                if (viewportBounds.IsValid)
                {
                    warmSeed = TryGetReliableFloorAlignment(
                        match,
                        clientBounds,
                        viewportBounds,
                        locked.Map,
                        floorKey,
                        out _);
                    // 稳态开图路径：游戏按键后大地图存在展开/淡入动画（约 50-80ms），
                    // 在第 0ms 进行单帧盲抓并强跑 VPSG 3.0 必然因画面未就绪而 100% 失败（实测 113 次全挂并平白消耗 30ms CPU）。
                    // 前台已有 5ms 级乐观预呈现保障视觉瞬发，后台直接通过轻量签名就绪检测等待大地图展开，避免无效算力争抢与伪 fallback。
                    canUseSteadyFastCapture = false;
                }
            }
        }

        var stableViewportMode = canUseSteadyFastCapture
            ? "steady-fast-capture"
            : "readiness";
        var stableViewportFallback = false;
        var stableViewportWait = 0d;
        CapturedGameFrame? frame = null;
        MapRecognitionAttempt? precomputedVpsg3Attempt = null;

        if (canUseSteadyFastCapture)
        {
            var fastCaptureTimer = stableViewportTimer;
            var stableViewport = ActiveOperationTrace?.StartTopLevel(
                "stable_viewport",
                MapOperationWaitKind.Capture,
                mapId: locked.Map.Id.ToString("D"),
                floorKey: floorKey);
            try
            {
                frame = await CaptureMapViewportOnceAsync(
                    "仅对齐",
                    viewport,
                    cancellationToken);
            }
            finally
            {
                stableViewport?.Complete();
                fastCaptureTimer.Stop();
                stableViewportWait += fastCaptureTimer.Elapsed.TotalMilliseconds;
            }

            if (frame is not null)
            {
                MapDiagnosticModeCapture.BeginMapOpen(frame.Image);
                var fastAlignment = ActiveOperationTrace?.StartTopLevel(
                    "alignment_compute",
                    MapOperationWaitKind.Compute,
                    route: "steady-fast-capture",
                    mapId: locked.Map.Id.ToString("D"),
                    floorKey: floorKey);
                try
                {
                    precomputedVpsg3Attempt = await Task.Run(() =>
                    {
                        return _recognition.TryAlignWithVpsg3(
                                frame,
                                locked.Map,
                                floorKey,
                                warmSeed!.Session.SideEntranceScanPriorConfidence,
                                out var attempt,
                                knownScaleSeed: warmSeed.Session.LockedTransform.ScaleX)
                            && attempt?.Recognition is not null
                                ? attempt
                                : null;
                    }, cancellationToken);
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
                finally
                {
                    fastAlignment?.Complete();
                }

                if (precomputedVpsg3Attempt is not null)
                {
                    return new MapOpenViewportCaptureResult(
                        frame,
                        stableViewportWait,
                        stableViewportMode,
                        stableViewportFallback,
                        precomputedVpsg3Attempt);
                }

                frame.Dispose();
                frame = null;
                stableViewportFallback = true;
                stableViewportMode = "steady-fast-capture-fallback";
            }
            else
            {
                stableViewportFallback = true;
                stableViewportMode = "steady-fast-capture-fallback";
            }
        }

        if (frame is null)
        {
            var fallbackTimer = canUseSteadyFastCapture
                ? Stopwatch.StartNew()
                : stableViewportTimer;
            var stableViewport = ActiveOperationTrace?.StartTopLevel(
                "stable_viewport",
                MapOperationWaitKind.Capture,
                mapId: locked.Map.Id.ToString("D"),
                floorKey: floorKey);
            try
            {
                frame = await CaptureStableViewportAsync(
                    "仅对齐",
                    cancellationToken,
                    relaxForLockedMap: true,
                    shouldContinue: () => _gameMapToggleState.IsCurrent(toggle),
                    lowStructureReadiness: channel.Channel == MapAlignmentChannel.LowStructure,
                    lowStructureReadinessFrameCount:
                        initialPrewarmTuning.LowStructureReadinessFrameCount);
            }
            finally
            {
                stableViewport?.Complete();
                fallbackTimer.Stop();
                stableViewportWait += fallbackTimer.Elapsed.TotalMilliseconds;
            }
        }

        return new MapOpenViewportCaptureResult(
            frame,
            stableViewportWait,
            stableViewportMode,
            stableViewportFallback,
            precomputedVpsg3Attempt);
    }

    private Task<CapturedGameFrame?> CaptureMapViewportOnceAsync(
        string operation,
        NormalizedRectangle viewport,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lastStableCaptureFailureReason = null;
        var captureSpan = ActiveOperationTrace?.StartChild(
            "capture",
            MapOperationWaitKind.Capture,
            attemptIndex: 1);
        try
        {
            var latestHit = _captureSvc.TryAcquireLatestViewportFrame(
                viewport,
                TimeSpan.FromMilliseconds(16),
                out var latestObject);
            if (latestHit && latestObject is CapturedGameFrame latest)
            {
                return Task.FromResult<CapturedGameFrame?>(latest);
            }
            if (latestHit && latestObject is IDisposable latestDisposable)
                DisposeViewportFrame(latestDisposable, 1);

            if (_captureSvc.TryCaptureViewport(
                    viewport,
                    out var frameObject,
                    out var failureReason)
                && frameObject is CapturedGameFrame captured)
            {
                return Task.FromResult<CapturedGameFrame?>(captured);
            }

            if (frameObject is IDisposable disposable)
                DisposeViewportFrame(disposable, 1);
            _lastStableCaptureFailureReason = string.IsNullOrWhiteSpace(failureReason)
                ? $"{operation} 截图失败。"
                : failureReason;
            return Task.FromResult<CapturedGameFrame?>(null);
        }
        finally
        {
            captureSpan?.Complete();
        }
    }
}
