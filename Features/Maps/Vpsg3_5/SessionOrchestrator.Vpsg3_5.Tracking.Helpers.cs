using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
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

            var viewport = ResolveTrackingViewport(useAutomaticCanvas);
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
