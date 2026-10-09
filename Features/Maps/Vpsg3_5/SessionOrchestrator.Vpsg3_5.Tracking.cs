using System.Diagnostics;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunVpsg3_5TrackingLoopAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition initialRecognition,
        MapOverlayTransform initialTransform,
        bool useAutomaticCanvas,
        CancellationToken cancellationToken)
    {
        var currentRecognition = initialRecognition;
        var lockedScale = (initialTransform.ScaleX + initialTransform.ScaleY) / 2.0d;
        var priorTx = initialTransform.OffsetX;
        var priorTy = initialTransform.OffsetY;
        var feedforwardTx = priorTx;
        var feedforwardTy = priorTy;
        var visualFrameTx = priorTx;
        var visualFrameTy = priorTy;
        var hasVisualFrameAnchor = false;
        var previousVisualTimestamp = 0L;
        var lastMotionSample = 0L;
        var velTx = 0.0d;
        var velTy = 0.0d;
        var weakFrames = 0;
        var trackingConfig = Vpsg3_5TrackingConfig.Default;
        var gameWindowHandle = IntPtr.Zero;

        var wasDragging = false;
        var mouseOwnsDragPosition = false;
        var mouseHistory = new TimestampedMouseHistory();
        using var opticalFlow = new Vpsg3_5OpticalFlowTracker();
        CancellationTokenSource? dragCaptureCancellation = null;
        Task<object?>? pendingCapture = null;
        long pendingCaptureStarted = 0;
        var afterSystemTicks = SystemRelativeClock.GetTicks();

        var sessionStarted = Stopwatch.GetTimestamp();
        var lastTelemetryLog = Stopwatch.GetTimestamp();
        var lastAbsoluteCorrection = 0L;
        var dragStarted = 0L;
        var dragDurationMs = 0d;
        var captureCount = 0;
        var gdiFallbackCount = 0;
        var visualAttempts = 0;
        var acceptedCount = 0;
        var rejectedCount = 0;
        var opticalAttempts = 0;
        var opticalAccepted = 0;
        var droppedFrames = 0;
        var captureAgeTotalMs = 0d;
        var readbackTotalMs = 0d;
        var preprocessTotalMs = 0d;
        var trackTotalMs = 0d;
        var endToEndTotalMs = 0d;
        var solveSamples = new List<double>();

        _realtimeMapTransformPublisher.Snapshot(reset: true);

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
                var isForeground = gameWindowHandle != IntPtr.Zero && ReadTrackingForegroundWindow() == gameWindowHandle;
                if (!isForeground)
                {
                    if (_captureSvc.TryGetForegroundClientBounds(out _, out var fgWindow, out _))
                    {
                        gameWindowHandle = fgWindow;
                        isForeground = true;
                    }
                }

                var isLButtonDown = ReadTrackingLeftButtonDown();
                var isDragging = isForeground && isLButtonDown;

                if (!isDragging)
                {
                    if (wasDragging)
                    {
                        // Mouse motion is transient display state, like the old
                        // helper. Keep it across presses without a blocking solve
                        // or a delayed persistent-pose writer. Reopening the map
                        // still goes through normal current-frame alignment.
                        wasDragging = false;
                        dragDurationMs += Stopwatch.GetElapsedTime(dragStarted).TotalMilliseconds;
                        await DisposePendingCaptureAsync(
                            pendingCapture,
                            dragCaptureCancellation).ConfigureAwait(false);
                        pendingCapture = null;
                        dragCaptureCancellation = null;
                        var snapResult = mouseOwnsDragPosition ? null : await PerformReleaseSnapAsync(
                            context,
                            currentRecognition,
                            lockedScale,
                            feedforwardTx,
                            feedforwardTy,
                            trackingConfig,
                            useAutomaticCanvas,
                            cancellationToken).ConfigureAwait(false);

                        if (snapResult is not null && snapResult.IsAccepted
                            && !ReadTrackingLeftButtonDown()
                            && IsOrbTrackingContextCurrent(context))
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
                            PublishRealtimeMapTransform(
                                context,
                                lockedScale,
                                feedforwardTx,
                                feedforwardTy,
                                SystemRelativeClock.GetTicks(),
                                snapResult.Confidence);
                        }

                        priorTx = feedforwardTx;
                        priorTy = feedforwardTy;
                        velTx = 0.0d;
                        velTy = 0.0d;
                        weakFrames = 0;
                        opticalFlow.Reset();
                        hasVisualFrameAnchor = false;
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
                    dragStarted = Stopwatch.GetTimestamp();
                    if (TryReadTrackingCursor(out var initialCursor))
                    {
                        mouseOwnsDragPosition = trackingConfig.EnableMouseFeedforward;
                        mouseHistory.Reset(
                            initialCursor.X,
                            initialCursor.Y,
                            SystemRelativeClock.GetTicks());
                    }
                    else
                    {
                        mouseOwnsDragPosition = false;
                    }
                    feedforwardTx = priorTx;
                    feedforwardTy = priorTy;
                    visualFrameTx = priorTx;
                    visualFrameTy = priorTy;
                    hasVisualFrameAnchor = false;
                    previousVisualTimestamp = 0L;
                    opticalFlow.Reset();
                    lastAbsoluteCorrection = Stopwatch.GetTimestamp();
                    afterSystemTicks = SystemRelativeClock.GetTicks();
                    _captureSvc.PrepareViewportCapture();
                    dragCaptureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                }

                // 1. Zero-latency Mouse Feedforward
                if (trackingConfig.EnableMouseFeedforward && TryReadTrackingCursor(out var currentCursor))
                {
                    var mouseTimestamp = SystemRelativeClock.GetTicks();
                    var (rawDx, rawDy) = mouseHistory.Record(
                        currentCursor.X,
                        currentCursor.Y,
                        mouseTimestamp,
                        trackingConfig.MouseScaleRatio);

                    if (rawDx != 0 || rawDy != 0)
                    {
                        feedforwardTx += rawDx;
                        feedforwardTy += rawDy;
                        PublishRealtimeMapTransform(
                            context,
                            lockedScale,
                            feedforwardTx,
                            feedforwardTy,
                            mouseTimestamp,
                            0.05d,
                            RealtimeTransformSource.MousePoll);
                    }
                }

                // 2. Consume each distinct WGC frame at most once. The task remains
                // pending while the 2ms cursor loop continues to publish transforms.
                if (pendingCapture is null && dragCaptureCancellation is not null)
                {
                    pendingCaptureStarted = Stopwatch.GetTimestamp();
                    pendingCapture = _captureSvc.CaptureNextViewportAsync(
                        ResolveMapViewportForCurrentWindow(),
                        afterSystemTicks,
                        TimeSpan.FromMilliseconds(trackingConfig.FrameCaptureWaitMs),
                        dragCaptureCancellation.Token);
                }

                if (pendingCapture?.IsCompleted == true)
                {
                    object? frameObject = null;
                    var captureElapsedMs = Stopwatch.GetElapsedTime(pendingCaptureStarted).TotalMilliseconds;
                    try
                    {
                        frameObject = await pendingCapture.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        dragCaptureCancellation?.IsCancellationRequested == true)
                    {
                    }
                    pendingCapture = null;

                    var fallbackTimestamp = SystemRelativeClock.GetTicks();
                    if (frameObject is not CapturedGameFrame)
                    {
                        var fallbackStarted = Stopwatch.GetTimestamp();
                        var fallbackCaptured = _captureSvc.TryCaptureViewport(
                            ResolveMapViewportForCurrentWindow(),
                            out frameObject,
                            out _);
                        captureElapsedMs = Stopwatch.GetElapsedTime(fallbackStarted).TotalMilliseconds;
                        if (fallbackCaptured)
                            gdiFallbackCount++;
                    }

                    if (frameObject is CapturedGameFrame frame)
                    {
                        if (useAutomaticCanvas)
                            frame = MapFrameUiExclusion.WithAutomaticCanvasContext(frame);
                        using (frame)
                        {
                            captureCount++;
                            droppedFrames += frame.CaptureDroppedFrames;
                            var frameTimestamp = frame.CaptureSystemRelativeTicks > 0
                                ? frame.CaptureSystemRelativeTicks
                                : fallbackTimestamp;
                            afterSystemTicks = Math.Max(afterSystemTicks, frameTimestamp);
                            var captureAgeMs = SystemRelativeClock.AgeMilliseconds(frameTimestamp);
                            captureAgeTotalMs += captureAgeMs;
                            readbackTotalMs += frame.CaptureReadbackMilliseconds > 0d
                                ? frame.CaptureReadbackMilliseconds
                                : captureElapsedMs;

                            opticalAttempts++;
                            var flow = opticalFlow.Track(frame.Image);
                            if (flow.Accepted)
                                opticalAccepted++;
                            preprocessTotalMs += flow.PreprocessMilliseconds;
                            trackTotalMs += flow.TrackMilliseconds;
                            var pendingMouse = mouseHistory.DeltaAfter(frameTimestamp);
                            var previousPendingMouse = mouseHistory.DeltaAfter(previousVisualTimestamp);
                            if (previousVisualTimestamp > 0 && ElapsedMilliseconds(lastMotionSample) >= 200)
                            {
                                lastMotionSample = Stopwatch.GetTimestamp();
                                LogTrackingMotionSample(context, frameTimestamp, previousVisualTimestamp,
                                    previousPendingMouse.Dx - pendingMouse.Dx,
                                    previousPendingMouse.Dy - pendingMouse.Dy,
                                    flow, feedforwardTx, feedforwardTy, visualFrameTx, visualFrameTy);
                            }
                            previousVisualTimestamp = frameTimestamp;
                            if (!hasVisualFrameAnchor)
                            {
                                visualFrameTx = feedforwardTx - pendingMouse.Dx;
                                visualFrameTy = feedforwardTy - pendingMouse.Dy;
                                hasVisualFrameAnchor = true;
                            }
                            else if (flow.Accepted && !mouseOwnsDragPosition)
                            {
                                visualFrameTx += flow.DeltaX;
                                visualFrameTy += flow.DeltaY;
                                feedforwardTx = visualFrameTx + pendingMouse.Dx;
                                feedforwardTy = visualFrameTy + pendingMouse.Dy;
                                PublishRealtimeMapTransform(
                                    context,
                                    lockedScale,
                                    feedforwardTx,
                                    feedforwardTy,
                                    frameTimestamp,
                                    flow.InlierRatio);
                            }
                            else
                            {
                                // During pointer-driven drag the visual observation is
                                // only a baseline. Delayed pixels or stationary scenery
                                // must not undo the mouse displacement already displayed.
                                visualFrameTx = feedforwardTx - pendingMouse.Dx;
                                visualFrameTy = feedforwardTy - pendingMouse.Dy;
                            }

                            var now = Stopwatch.GetTimestamp();
                            if (!mouseOwnsDragPosition && ElapsedMilliseconds(lastAbsoluteCorrection)
                                >= trackingConfig.VisualVerificationIntervalMs)
                            {
                                lastAbsoluteCorrection = now;
                                visualAttempts++;
                                var preprocessStarted = Stopwatch.GetTimestamp();
                                using var obs = Vpsg3FastLiveExtractor.Extract(
                                    frame.Image,
                                    frame.ViewportBounds,
                                    excludedScreenRegions: frame.UiExclusionRegions);
                                preprocessTotalMs += Stopwatch.GetElapsedTime(preprocessStarted)
                                    .TotalMilliseconds;
                                if (obs.SparseEdgePoints.Count >= 8
                                    && _recognition.TryGetVpsg3FloorLease(
                                        currentRecognition.Map,
                                        context.FloorKey,
                                        out var lease))
                                {
                                    Vpsg3_5TrackingResult trackResult;
                                    using (lease)
                                    {
                                        trackResult = Vpsg3_5TrackingSolver.TryTrack(
                                            obs,
                                            lease.Floor,
                                            lockedScale,
                                            visualFrameTx,
                                            visualFrameTy,
                                            trackingConfig);
                                    }
                                    solveSamples.Add(trackResult.Timing.TotalMs);
                                    LogTrackingCorrectionSample(context, frameTimestamp, trackResult,
                                        visualFrameTx, visualFrameTy, feedforwardTx, feedforwardTy);
                                    if (TryReadTrackingCursor(out var cursorAfterSolve))
                                    {
                                        var postSolveMouse = mouseHistory.Record(
                                            cursorAfterSolve.X,
                                            cursorAfterSolve.Y,
                                            SystemRelativeClock.GetTicks(),
                                            trackingConfig.MouseScaleRatio);
                                        feedforwardTx += postSolveMouse.Dx;
                                        feedforwardTy += postSolveMouse.Dy;
                                    }
                                    if (trackResult.IsAccepted)
                                    {
                                        acceptedCount++;
                                        weakFrames = 0;
                                        _alignmentTrackingMode = MapAlignmentTrackingMode.VpsgTracking;
                                        visualFrameTx = trackResult.OffsetX;
                                        visualFrameTy = trackResult.OffsetY;
                                        var mouseAfterCapture = mouseHistory.DeltaAfter(frameTimestamp);
                                        feedforwardTx = visualFrameTx + mouseAfterCapture.Dx;
                                        feedforwardTy = visualFrameTy + mouseAfterCapture.Dy;
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
                                        PublishRealtimeMapTransform(
                                            context,
                                            lockedScale,
                                            feedforwardTx,
                                            feedforwardTy,
                                            frameTimestamp,
                                            trackResult.Confidence);
                                    }
                                    else
                                    {
                                        rejectedCount++;
                                        weakFrames++;
                                        HandleWeakTrackingFrame(context, ref currentRecognition, lockedScale, ref priorTx, ref priorTy, ref velTx, ref velTy, ref weakFrames);
                                    }
                                }
                                else
                                {
                                    rejectedCount++;
                                    weakFrames++;
                                    HandleWeakTrackingFrame(context, ref currentRecognition, lockedScale, ref priorTx, ref priorTy, ref velTx, ref velTy, ref weakFrames);
                                }
                            }
                            endToEndTotalMs += SystemRelativeClock.AgeMilliseconds(frameTimestamp);
                        }
                    }
                    else
                    {
                        weakFrames++;
                        droppedFrames++;
                    }
                }

                // 3. Periodic Telemetry (Every 3s)
                if (ElapsedMilliseconds(lastTelemetryLog) >= 3000)
                {
                    var activeDragMs = dragDurationMs
                        + (wasDragging
                            ? Stopwatch.GetElapsedTime(dragStarted).TotalMilliseconds
                            : 0d);
                    var fps = activeDragMs > 0d
                        ? captureCount / (activeDragMs / 1000d)
                        : 0d;
                    var hitRate = visualAttempts > 0 ? (double)acceptedCount / visualAttempts : 0d;
                    var avgMs = solveSamples.Count > 0 ? solveSamples.Average() : 0d;
                    var realtime = _realtimeMapTransformPublisher.Snapshot(reset: false);
                    lastTelemetryLog = Stopwatch.GetTimestamp();

                    _logCollector.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Info,
                        $"VPSG 3.5 hybrid tracking telemetry · fps={fps:F1} · hitRate={hitRate:P0} · avgSolve={avgMs:F2}ms · mode={_alignmentTrackingMode}",
                        details: new()
                        {
                            ["generation"] = context.Generation,
                            ["captureCount"] = captureCount,
                            ["visualAttempts"] = visualAttempts,
                            ["accepted"] = acceptedCount,
                            ["rejected"] = rejectedCount,
                            ["opticalAttempts"] = opticalAttempts,
                            ["opticalAccepted"] = opticalAccepted,
                            ["dispatcherQueueMs"] = realtime.AverageDispatcherQueueMs,
                            ["dispatcherQueueP95Ms"] = realtime.P95DispatcherQueueMs,
                            ["renderMs"] = realtime.AverageRenderMs,
                            ["renderP95Ms"] = realtime.P95RenderMs,
                            ["endToEndMs"] = realtime.AverageEndToEndMs,
                            ["endToEndP95Ms"] = realtime.P95EndToEndMs,
                            ["timingEndpoint"] = "overlay-callback-return-not-screen-presentation",
                            ["mousePollSamples"] = realtime.MouseSampleCount,
                            ["mousePollToCallbackP95Ms"] = realtime.MouseCallbackP95Ms,
                            ["visualFrameSamples"] = realtime.VisualSampleCount,
                            ["visualFrameToCallbackP95Ms"] = realtime.VisualCallbackP95Ms,
                            ["droppedFrames"] = droppedFrames,
                            ["coalescedTransforms"] = realtime.CoalescedTransforms
                        });
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
            if (wasDragging)
                dragDurationMs += Stopwatch.GetElapsedTime(dragStarted).TotalMilliseconds;
            await DisposePendingCaptureAsync(
                pendingCapture,
                dragCaptureCancellation).ConfigureAwait(false);
            var sessionDurationMs = Stopwatch.GetElapsedTime(sessionStarted).TotalMilliseconds;
            LogVpsg3_5SessionEnd(
                context,
                sessionDurationMs,
                dragDurationMs,
                captureCount,
                gdiFallbackCount,
                visualAttempts,
                acceptedCount,
                rejectedCount,
                opticalAttempts,
                droppedFrames,
                captureAgeTotalMs,
                readbackTotalMs,
                preprocessTotalMs,
                trackTotalMs,
                endToEndTotalMs,
                solveSamples);
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

}
