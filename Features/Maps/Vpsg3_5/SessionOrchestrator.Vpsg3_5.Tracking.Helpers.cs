using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void LogTrackingMotionSample(OrbTrackingContext context, long frameTicks,
        long previousFrameTicks, double mouseDx, double mouseDy, Vpsg3_5OpticalFlowResult flow,
        double tx, double ty, double visualTx, double visualTy) =>
        _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "拖动鼠标与画面位移对照", details: new()
            {
                ["generation"] = context.Generation, ["frameTicks"] = frameTicks,
                ["previousFrameTicks"] = previousFrameTicks,
                ["mouseDx"] = mouseDx, ["mouseDy"] = mouseDy,
                ["flowAccepted"] = flow.Accepted, ["flowDx"] = flow.DeltaX, ["flowDy"] = flow.DeltaY,
                ["feedforwardTx"] = tx, ["feedforwardTy"] = ty,
                ["visualTx"] = visualTx, ["visualTy"] = visualTy
            });

    private void LogTrackingCorrectionSample(OrbTrackingContext context, long frameTicks,
        Vpsg3_5TrackingResult result, double priorTx, double priorTy, double tx, double ty) =>
        _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "拖动结构修正位置对照", details: new()
            {
                ["generation"] = context.Generation, ["frameTicks"] = frameTicks,
                ["accepted"] = result.IsAccepted, ["failureReason"] = result.FallbackReason,
                ["priorTx"] = priorTx, ["priorTy"] = priorTy,
                ["solvedTx"] = result.OffsetX, ["solvedTy"] = result.OffsetY,
                ["feedforwardTx"] = tx, ["feedforwardTy"] = ty
            });

    private const int VkLButton = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private IntPtr ReadTrackingForegroundWindow() =>
        _mapDragInput is { } source ? source.GetForegroundWindow() : GetForegroundWindow();

    private bool ReadTrackingLeftButtonDown() =>
        _mapDragInput is { } source
            ? source.IsLeftButtonDown()
            : (GetAsyncKeyState(VkLButton) & 0x8000) != 0;

    private bool TryReadTrackingCursor(out NativePoint point)
    {
        if (_mapDragInput is not { } source)
            return GetCursorPos(out point);
        var available = source.TryGetCursorPosition(out var x, out var y);
        point = new NativePoint { X = x, Y = y };
        return available;
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private void HandleWeakTrackingFrame(
        OrbTrackingContext context,
        ref RuntimeMapRecognition currentRecognition,
        double lockedScale,
        ref double priorTx,
        ref double priorTy,
        ref double velTx,
        ref double velTy,
        ref int weakFrames)
    {
        // A weak frame must never extrapolate the last accepted translation.
        velTx = 0.0d;
        velTy = 0.0d;
        if (weakFrames >= 5)
            _alignmentTrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
        if (weakFrames >= 30)
            _alignmentTrackingMode = MapAlignmentTrackingMode.Lost;
    }

    private async Task<Vpsg3_5TrackingResult?> PerformReleaseSnapAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition currentRecognition,
        double lockedScale,
        double feedforwardTx,
        double feedforwardTy,
        Vpsg3_5TrackingConfig trackingConfig,
        bool useAutomaticCanvas,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            if (!IsOrbTrackingContextCurrent(context))
                return null;

            var viewport = ResolveMapViewportForCurrentWindow();
            object? frameObject = await _captureSvc.CaptureNextViewportAsync(
                viewport,
                SystemRelativeClock.GetTicks(),
                TimeSpan.FromMilliseconds(trackingConfig.FrameCaptureWaitMs),
                cancellationToken).ConfigureAwait(false);
            if (frameObject is not CapturedGameFrame)
                _captureSvc.TryCaptureViewport(viewport, out frameObject, out _);
            if (frameObject is not CapturedGameFrame frame)
                return null;

            if (useAutomaticCanvas)
                frame = MapFrameUiExclusion.WithAutomaticCanvasContext(frame);
            using (frame)
            using (var observation = Vpsg3FastLiveExtractor.Extract(
                       frame.Image,
                       frame.ViewportBounds,
                       excludedScreenRegions: frame.UiExclusionRegions))
            {
                if (observation.SparseEdgePoints.Count < 8
                    || !_recognition.TryGetVpsg3FloorLease(
                        currentRecognition.Map,
                        context.FloorKey,
                        out var lease))
                {
                    return null;
                }
                using (lease)
                {
                    return Vpsg3_5TrackingSolver.TryTrack(
                        observation,
                        lease.Floor,
                        lockedScale,
                        feedforwardTx,
                        feedforwardTy,
                        trackingConfig);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task DisposePendingCaptureAsync(
        Task<object?>? pendingCapture,
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        try
        {
            cancellation.Cancel();
            if (pendingCapture is not null)
            {
                var frame = await pendingCapture.ConfigureAwait(false);
                (frame as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Session teardown must still release the timer period and tracking state.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void LogVpsg3_5SessionEnd(
        OrbTrackingContext context,
        double sessionDurationMs,
        double dragDurationMs,
        int captureCount,
        int gdiFallbackCount,
        int visualAttempts,
        int acceptedCount,
        int rejectedCount,
        int opticalAttempts,
        int droppedFrames,
        double captureAgeTotalMs,
        double readbackTotalMs,
        double preprocessTotalMs,
        double trackTotalMs,
        double endToEndTotalMs,
        List<double> solveSamples)
    {
        var realtime = _realtimeMapTransformPublisher.Snapshot(reset: false);
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"VPSG 3.5 session-end telemetry · captures={captureCount} · accepted={acceptedCount}/{visualAttempts} · drag={dragDurationMs:F0}ms",
            elapsedMs: sessionDurationMs,
            details: new()
            {
                ["generation"] = context.Generation,
                ["captureCount"] = captureCount,
                ["gdiFallbackCount"] = gdiFallbackCount,
                ["visualAttempts"] = visualAttempts,
                ["accepted"] = acceptedCount,
                ["rejected"] = rejectedCount,
                ["avgSolveMs"] = solveSamples.Count > 0 ? solveSamples.Average() : 0d,
                ["p95SolveMs"] = Percentile95(solveSamples),
                ["dragDurationMs"] = dragDurationMs,
                ["captureAgeMs"] = Average(captureAgeTotalMs, captureCount),
                ["readbackMs"] = Average(readbackTotalMs, captureCount),
                ["preprocessMs"] = Average(preprocessTotalMs, Math.Max(1, opticalAttempts + visualAttempts)),
                ["trackMs"] = Average(trackTotalMs, opticalAttempts),
                ["dispatcherQueueMs"] = realtime.AverageDispatcherQueueMs,
                ["dispatcherQueueP95Ms"] = realtime.P95DispatcherQueueMs,
                ["renderMs"] = realtime.AverageRenderMs,
                ["renderP95Ms"] = realtime.P95RenderMs,
                ["pipelineUntilPublishMs"] = Average(endToEndTotalMs, captureCount),
                ["endToEndMs"] = realtime.AverageEndToEndMs,
                ["endToEndP95Ms"] = realtime.P95EndToEndMs,
                ["timingEndpoint"] = "overlay-callback-return-not-screen-presentation",
                ["mousePollSamples"] = realtime.MouseSampleCount,
                ["mousePollToCallbackP95Ms"] = realtime.MouseCallbackP95Ms,
                ["visualFrameSamples"] = realtime.VisualSampleCount,
                ["visualFrameToCallbackP95Ms"] = realtime.VisualCallbackP95Ms,
                ["droppedFrames"] = droppedFrames,
                ["coalescedTransforms"] = realtime.CoalescedTransforms,
                ["publishedTransforms"] = realtime.PublishedTransforms,
                ["appliedTransforms"] = realtime.AppliedTransforms,
                ["droppedTransforms"] = realtime.DroppedTransforms
            });
    }

    private static double Average(double total, int count) =>
        count > 0 ? total / count : 0d;

    private static double Percentile95(List<double> samples)
    {
        if (samples.Count == 0)
            return 0d;
        var ordered = samples.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * 0.95d) - 1];
    }
}
