using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Models;

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
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private async Task RunVpsg3_5TrackingLoopAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition initialRecognition,
        MapOverlayTransform initialTransform,
        CancellationToken cancellationToken)
    {
        var currentRecognition = initialRecognition;
        var lockedScale = (initialTransform.ScaleX + initialTransform.ScaleY) / 2.0d;
        var priorTx = initialTransform.OffsetX;
        var priorTy = initialTransform.OffsetY;
        var feedforwardTx = priorTx;
        var feedforwardTy = priorTy;
        var velTx = 0.0d;
        var velTy = 0.0d;
        var weakFrames = 0;
        var trackingConfig = Vpsg3_5TrackingConfig.Default;
        var gameWindowHandle = IntPtr.Zero;

        var wasDragging = false;
        var lastCursor = default(NativePoint);
        var hasCursorSample = false;

        var lastTelemetryLog = Stopwatch.GetTimestamp();
        var lastVisualSolveTimestamp = 0L;
        var fpsCounter = 0;
        var hitsCounter = 0;
        var totalSolverMs = 0.0d;

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"VPSG 3.5 hybrid tracking loop entered · map={context.MapId} · floor={context.FloorKey} · scale={lockedScale:F4} · initialTx={priorTx:F1}, initialTy={priorTy:F1}");

        TimeBeginPeriod(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && IsOrbTrackingContextCurrent(context))
            {
                // Check if game window is foreground and if mouse left button is held
                var isForeground = gameWindowHandle != IntPtr.Zero && GetForegroundWindow() == gameWindowHandle;
                if (!isForeground)
                {
                    if (_captureSvc.TryGetForegroundClientBounds(out _, out var fgWindow, out _))
                    {
                        gameWindowHandle = fgWindow;
                        isForeground = true;
                    }
                }

                var isLButtonDown = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
                var isDragging = isForeground && isLButtonDown;

                if (!isDragging)
                {
                    if (wasDragging)
                    {
                        // Release Snap: player just released mouse button.
                        // Perform one clean visual capture and solve to snap strictly to ground truth.
                        wasDragging = false;
                        var snapResult = await PerformReleaseSnapAsync(
                            context,
                            currentRecognition,
                            lockedScale,
                            feedforwardTx,
                            feedforwardTy,
                            trackingConfig);

                        if (snapResult is not null && snapResult.IsAccepted)
                        {
                            feedforwardTx = snapResult.OffsetX;
                            feedforwardTy = snapResult.OffsetY;

                            var finalTransform = MapCanonicalTransformMath.BuildOverlayTransform(
                                lockedScale,
                                lockedScale,
                                feedforwardTx,
                                feedforwardTy,
                                currentRecognition.Result.OverlayTransform?.ReferenceWidth ?? 1000,
                                currentRecognition.Result.OverlayTransform?.ReferenceHeight ?? 1000,
                                residualPixels: 0d,
                                orientationDegrees: currentRecognition.Result.OrientationDegrees,
                                alignmentMode: MapOverlayAlignmentMode.Uniform);

                            var finalRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                currentRecognition,
                                finalTransform,
                                MapRecognitionSource.VpsgTracking);

                            currentRecognition = finalRecognition;
                            EnqueueOrbTrackingCommit(context, finalRecognition, 0.05d);
                        }

                        priorTx = feedforwardTx;
                        priorTy = feedforwardTy;
                        velTx = 0.0d;
                        velTy = 0.0d;
                        weakFrames = 0;
                        _alignmentTrackingMode = MapAlignmentTrackingMode.VpsgTracking;
                    }

                    // Map cannot move in-game without holding Left Mouse Button.
                    // Keep overlay strictly stationary: 0 jitter, zero CPU waste.
                    velTx = 0.0d;
                    velTy = 0.0d;
                    weakFrames = 0;
                    _alignmentTrackingMode = MapAlignmentTrackingMode.VpsgTracking;
                    await Task.Delay(30, cancellationToken);
                    continue;
                }

                // Drag transition: newly pressed
                if (!wasDragging)
                {
                    wasDragging = true;
                    hasCursorSample = GetCursorPos(out lastCursor);
                    feedforwardTx = priorTx;
                    feedforwardTy = priorTy;
                    lastVisualSolveTimestamp = Stopwatch.GetTimestamp();
                }

                // 1. Zero-latency Mouse Feedforward
                if (trackingConfig.EnableMouseFeedforward && GetCursorPos(out var currentCursor))
                {
                    var rawDx = hasCursorSample
                        ? (double)(currentCursor.X - lastCursor.X) * trackingConfig.MouseScaleRatio
                        : 0d;
                    var rawDy = hasCursorSample
                        ? (double)(currentCursor.Y - lastCursor.Y) * trackingConfig.MouseScaleRatio
                        : 0d;
                    lastCursor = currentCursor;
                    hasCursorSample = true;

                    if (rawDx != 0 || rawDy != 0)
                    {
                        feedforwardTx += rawDx;
                        feedforwardTy += rawDy;

                        // Immediate overlay update
                        var feedforwardTransform = MapCanonicalTransformMath.BuildOverlayTransform(
                            lockedScale,
                            lockedScale,
                            feedforwardTx,
                            feedforwardTy,
                            currentRecognition.Result.OverlayTransform?.ReferenceWidth ?? 1000,
                            currentRecognition.Result.OverlayTransform?.ReferenceHeight ?? 1000,
                            residualPixels: 0d,
                            orientationDegrees: currentRecognition.Result.OrientationDegrees,
                            alignmentMode: MapOverlayAlignmentMode.Uniform);

                        var feedforwardRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                            currentRecognition,
                            feedforwardTransform,
                            MapRecognitionSource.VpsgTracking);

                        currentRecognition = feedforwardRecognition;
                        EnqueueOrbTrackingCommit(context, feedforwardRecognition, 0.05d);
                    }
                }

                // 2. Asynchronous Background Visual Verification
                var now = Stopwatch.GetTimestamp();
                var elapsedSinceVisualMs = (double)(now - lastVisualSolveTimestamp) * 1000.0d / Stopwatch.Frequency;
                if (elapsedSinceVisualMs >= trackingConfig.VisualVerificationIntervalMs)
                {
                    lastVisualSolveTimestamp = now;

                    var captureTx = feedforwardTx;
                    var captureTy = feedforwardTy;

                    if (_captureSvc.TryCaptureViewport(
                            ResolveMapViewportForCurrentWindow(),
                            out var frameObject,
                            out _)
                        && frameObject is CapturedGameFrame frame)
                    {
                        using (frame)
                        {
                            using var obs = Vpsg3FastLiveExtractor.Extract(frame.Image, frame.ViewportBounds);
                            if (obs.SparseEdgePoints.Count >= 8
                                && _recognition.TryGetVpsg3FloorLease(currentRecognition.Map, context.FloorKey, out var lease))
                            {
                                Vpsg3_5TrackingResult trackResult;
                                using (lease)
                                {
                                    trackResult = Vpsg3_5TrackingSolver.TryTrack(
                                        obs,
                                        lease.Floor,
                                        lockedScale,
                                        captureTx,
                                        captureTy,
                                        trackingConfig);
                                }

                                fpsCounter++;
                                totalSolverMs += trackResult.Timing.TotalMs;

                                if (trackResult.IsAccepted)
                                {
                                    hitsCounter++;
                                    weakFrames = 0;
                                    _alignmentTrackingMode = MapAlignmentTrackingMode.VpsgTracking;

                                    // The screenshot is the observed map position. Rebase the
                                    // prediction on it at every speed, then account for mouse
                                    // movement that arrived while capture and solving ran.
                                    var pendingDx = 0.0d;
                                    var pendingDy = 0.0d;
                                    if (GetCursorPos(out var cursorAfterSolve))
                                    {
                                        if (hasCursorSample)
                                        {
                                            pendingDx = (cursorAfterSolve.X - lastCursor.X) * trackingConfig.MouseScaleRatio;
                                            pendingDy = (cursorAfterSolve.Y - lastCursor.Y) * trackingConfig.MouseScaleRatio;
                                        }
                                        lastCursor = cursorAfterSolve;
                                        hasCursorSample = true;
                                    }
                                    feedforwardTx = trackResult.OffsetX + pendingDx;
                                    feedforwardTy = trackResult.OffsetY + pendingDy;
                                    priorTx = feedforwardTx;
                                    priorTy = feedforwardTy;

                                    var correctedTransform = MapCanonicalTransformMath.BuildOverlayTransform(
                                        lockedScale,
                                        lockedScale,
                                        feedforwardTx,
                                        feedforwardTy,
                                        currentRecognition.Result.OverlayTransform?.ReferenceWidth ?? 1000,
                                        currentRecognition.Result.OverlayTransform?.ReferenceHeight ?? 1000,
                                        residualPixels: 0d,
                                        orientationDegrees: currentRecognition.Result.OrientationDegrees,
                                        alignmentMode: MapOverlayAlignmentMode.Uniform);
                                    currentRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                        currentRecognition,
                                        correctedTransform,
                                        MapRecognitionSource.VpsgTracking);
                                    EnqueueOrbTrackingCommit(context, currentRecognition, 0.05d);
                                }
                                else
                                {
                                    weakFrames++;
                                    HandleWeakTrackingFrame(context, ref currentRecognition, lockedScale, ref priorTx, ref priorTy, ref velTx, ref velTy, ref weakFrames);
                                }
                            }
                            else
                            {
                                weakFrames++;
                                HandleWeakTrackingFrame(context, ref currentRecognition, lockedScale, ref priorTx, ref priorTy, ref velTx, ref velTy, ref weakFrames);
                            }
                        }
                    }
                    else
                    {
                        weakFrames++;
                    }
                }

                // 3. Periodic Telemetry (Every 3s)
                if (ElapsedMilliseconds(lastTelemetryLog) >= 3000)
                {
                    var elapsedSec = (double)(Stopwatch.GetTimestamp() - lastTelemetryLog) / Stopwatch.Frequency;
                    var fps = elapsedSec > 0 ? fpsCounter / elapsedSec : 0d;
                    var hitRate = fpsCounter > 0 ? (double)hitsCounter / fpsCounter : 0d;
                    var avgMs = fpsCounter > 0 ? totalSolverMs / fpsCounter : 0d;
                    lastTelemetryLog = Stopwatch.GetTimestamp();
                    fpsCounter = 0;
                    hitsCounter = 0;
                    totalSolverMs = 0d;

                    _logCollector.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Info,
                        $"VPSG 3.5 hybrid tracking telemetry · fps={fps:F1} · hitRate={hitRate:P0} · avgSolve={avgMs:F2}ms · mode={_alignmentTrackingMode}");
                }

                // High-frequency yield (2ms) for buttery smooth cursor polling
                await Task.Delay(2, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                "VPSG 3.5 tracking loop stopped after an unexpected error.",
                details: new()
                {
                    ["generation"] = context.Generation,
                    ["exception"] = exception.ToString()
                });
        }
        finally
        {
            TimeEndPeriod(1);
            lock (_orbTrackingGate)
            {
                if (_orbTrackingGeneration == context.Generation)
                {
                    _orbTrackingCancellation?.Dispose();
                    _orbTrackingCancellation = null;
                    _orbTrackingTask = null;
                }
            }
        }
    }

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
        // When tracking is weak, NEVER extrapolate or modify priorTx/priorTy!
        // Reset velocity so no runaway inertia is propagated.
        velTx = 0.0d;
        velTy = 0.0d;
        if (weakFrames >= 5)
        {
            _alignmentTrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
        }
        if (weakFrames >= 30)
        {
            _alignmentTrackingMode = MapAlignmentTrackingMode.Lost;
        }
    }

    private async Task<Vpsg3_5TrackingResult?> PerformReleaseSnapAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition currentRecognition,
        double lockedScale,
        double feedforwardTx,
        double feedforwardTy,
        Vpsg3_5TrackingConfig trackingConfig)
    {
        try
        {
            // Allow 25ms for the game's internal dragging lerp to settle on the final accurate position
            await Task.Delay(25);
            if (!IsOrbTrackingContextCurrent(context))
                return null;

            if (_captureSvc.TryCaptureViewport(
                    ResolveMapViewportForCurrentWindow(),
                    out var frameObject,
                    out _)
                && frameObject is CapturedGameFrame frame)
            {
                using (frame)
                {
                    using var obs = Vpsg3FastLiveExtractor.Extract(frame.Image, frame.ViewportBounds);
                    if (obs.SparseEdgePoints.Count >= 8
                        && _recognition.TryGetVpsg3FloorLease(currentRecognition.Map, context.FloorKey, out var lease))
                    {
                        using (lease)
                        {
                            return Vpsg3_5TrackingSolver.TryTrack(
                                obs,
                                lease.Floor,
                                lockedScale,
                                feedforwardTx,
                                feedforwardTy,
                                trackingConfig);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore release snap failures, keep last feedforward
        }
        return null;
    }
}
