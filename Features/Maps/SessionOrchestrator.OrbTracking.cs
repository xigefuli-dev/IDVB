using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly object _orbTrackingGate = new();
    private CancellationTokenSource? _orbTrackingCancellation;
    private Task? _orbTrackingTask;
    private Task? _retiredOrbTrackingTask;
    private long _orbTrackingGeneration;
    private long _lastOrbRenderMetricsTimestamp;
    private int _orbCommitQueued;
    private int _orbCaptureExclusionWarningLogged;
    private RuntimeMapRecognition? _pendingOrbTrackingRecognition;

    private async Task StartOrbTrackingAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame seedFrame)
    {
        var scan = ScanExecutionContext.Current;
        CancelOrbTracking("alignment replaced");
        await DrainOrbTrackingAsync();
        if (scan is not null && (scan.IsSuperseded || scan.CancellationToken.IsCancellationRequested
            || scan.Expired || !ReferenceEquals(_lastRecognition, recognition)))
            return;
        if (recognition.ReferencePythonValidated)
        {
            StartReferencePythonTracking(recognition, seedFrame);
            return;
        }
        var floorKey = recognition.Result.Floor;
        var useVpsgTracking = _recognition.IsVpsg3Ready(recognition.Map, floorKey);
        if (!useVpsgTracking && _settings?.EnableContinuousAlignment != true)
            return;

        var config = _config.Get<OrbTrackingConfig>("orb_tracking");
        if ((!useVpsgTracking && !config.Enabled && !IsAdaptiveScaleEnabled)
            || recognition.Result.OverlayTransform is not { } transform
            || !_gameMapToggleState.IsOpen
            || !_matchSession.Snapshot.IsStarted)
        {
            return;
        }

        if (!EnsureOverlayCaptureExclusion(
                "ORB tracking disabled because the overlay is not excluded from capture.",
                "Capture exclusion is disabled or not requested."))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _orbTrackingGeneration);
        _realtimeTransformReferenceWidth = transform.ReferenceWidth;
        _realtimeTransformReferenceHeight = transform.ReferenceHeight;
        _realtimeTransformOrientationDegrees = recognition.Result.OrientationDegrees;
        var context = new OrbTrackingContext(
            generation,
            _matchSession.Snapshot,
            new MapGameToggleTransition(true, _gameMapToggleState.Version),
            recognition.Map.Id,
            recognition.Map.UpdatedAt,
            floorKey,
            CreateAdaptiveScaleKey(
                seedFrame,
                recognition.Map,
                floorKey),
            (transform.ScaleX + transform.ScaleY) / 2d);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            CurrentMatchCancellationToken,
            _lifetimeCts.Token);
        var viewportBounds = seedFrame.ViewportBounds;
        var seed = config.Enabled ? seedFrame.Image.Clone() : null;
        lock (_orbTrackingGate)
        {
            _orbTrackingCancellation = linked;
            // Tracking owns a new lifecycle. It must not inherit the completed scan's
            // deadline or its soon-to-be-disposed observation through Task.Run.
            using var suppressScan = ScanExecutionContext.Suppress();
            _orbTrackingTask = useVpsgTracking
                ? Task.Run(
                    () => RunVpsg3_5TrackingLoopAsync(
                        context,
                        recognition,
                        transform,
                        linked.Token))
                : config.Enabled
                    ? Task.Run(
                        () => RunOrbTrackingLoopAsync(
                            context,
                            recognition,
                            seed!,
                            viewportBounds,
                            transform,
                            config,
                            linked.Token))
                    : Task.Run(
                        () => RunAdaptiveStructureTrackingLoopAsync(
                            context,
                            recognition,
                            config,
                            linked.Token));
        }
        _logCollector.Append(
            useVpsgTracking
                ? MapLogCategory.StructureRegistration
                : (config.Enabled ? MapLogCategory.OrbTracking : MapLogCategory.StructureRegistration),
            MapLogLevel.Info,
            useVpsgTracking
                ? $"VPSG 3.5 tracking started · map={context.MapId} · floor={context.FloorKey} · generation={generation}"
                : $"ORB tracking started · map={context.MapId} · floor={context.FloorKey} · generation={generation}");
    }

    private async Task RunOrbTrackingLoopAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        Mat seed,
        MapScreenRect seedViewportBounds,
        MapOverlayTransform initialTransform,
        OrbTrackingConfig config,
        CancellationToken cancellationToken)
    {
        try
        {
            using (seed)
            using (var tracker = new MapOrbTracker(
                seed,
                seedViewportBounds,
                initialTransform,
                MapOrbTrackingOptions.FromConfig(
                    config,
                    _settings?.SessionTuning.ViewportIgnoreRegions)))
            {
                var currentRecognition = recognition;
                var weakFrames = 0;
                var stableFrames = 0;
                var lastObservation = Stopwatch.GetTimestamp();
                var lastStructureCorrection = Stopwatch.GetTimestamp();
                var lastMetricsLog = Stopwatch.GetTimestamp();
                while (!cancellationToken.IsCancellationRequested
                    && IsOrbTrackingContextCurrent(context))
                {
                    var delay = stableFrames >= Math.Max(1, config.StableObservationCount)
                        ? Math.Max(20, config.StableIntervalMs)
                        : Math.Max(20, config.ActiveIntervalMs);
                    await Task.Delay(delay, cancellationToken);
                    if (!IsOrbTrackingContextCurrent(context))
                        break;

                    var captureTimer = Stopwatch.StartNew();
                    if (!_captureSvc.TryCaptureViewport(
                            ResolveMapViewportForCurrentWindow(),
                            out var frameObject,
                            out var captureFailure)
                        || frameObject is not CapturedGameFrame frame)
                    {
                        weakFrames++;
                        stableFrames = 0;
                        if (ElapsedMilliseconds(lastMetricsLog) >= 5000)
                        {
                            lastMetricsLog = Stopwatch.GetTimestamp();
                            LogOrbMetrics(
                                context,
                                "capture-rejected",
                                captureTimer.Elapsed.TotalMilliseconds,
                                0,
                                0,
                                weakFrames,
                                captureFailure);
                        }
                        continue;
                    }

                    using (frame)
                    {
                        captureTimer.Stop();
                        var now = Stopwatch.GetTimestamp();
                        var actualInterval = TimeSpan.FromSeconds(
                            (double)(now - lastObservation) / Stopwatch.Frequency);
                        lastObservation = now;
                        var orbTimer = Stopwatch.StartNew();
                        var observation = tracker.Track(
                            frame.Image,
                            frame.ViewportBounds,
                            actualInterval);
                        orbTimer.Stop();
                        if (observation.Accepted)
                        {
                            weakFrames = 0;
                            stableFrames = observation.ShouldCommit
                                ? 0
                                : stableFrames + 1;
                            var adaptiveOrb = EvaluateAdaptiveOrb(
                                context,
                                observation.Transform,
                                observation.StepScale);
                            if (adaptiveOrb.Reanchor)
                            {
                                tracker.Reanchor(
                                    frame.Image,
                                    frame.ViewportBounds,
                                    adaptiveOrb.Transform);
                            }
                            currentRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                currentRecognition,
                                adaptiveOrb.Transform,
                                MapRecognitionSource.OrbTracking);
                            if (observation.ShouldCommit)
                            {
                                EnqueueOrbTrackingCommit(
                                    context,
                                    MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                        currentRecognition,
                                        adaptiveOrb.Transform,
                                        MapRecognitionSource.OrbTracking),
                                    config.MaximumBaselineScaleChangeRatio);
                            }
                        }
                        else
                        {
                            weakFrames++;
                            stableFrames = 0;
                        }

                        var recoveryMode = weakFrames >= Math.Max(1, config.WeakFrameThreshold);
                        var correctionInterval = recoveryMode
                            ? Math.Max(100, config.RecoveryIntervalMs)
                            : Math.Max(250, config.StructureCorrectionIntervalMs);
                        correctionInterval = GetAdaptiveStructureProbeInterval(
                            context,
                            correctionInterval);
                        var structureMilliseconds = 0d;
                        if (ElapsedMilliseconds(lastStructureCorrection) >= correctionInterval)
                        {
                            lastStructureCorrection = Stopwatch.GetTimestamp();
                            var structureTimer = Stopwatch.StartNew();
                            var predictedRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                currentRecognition,
                                currentRecognition.Result.OverlayTransform
                                    ?? tracker.CurrentTransform,
                                MapRecognitionSource.OrbTracking);
                            var corrected = TryCorrectOrbTrackingWithStructure(
                                context,
                                frame,
                                predictedRecognition,
                                config.MaximumBaselineScaleChangeRatio,
                                context.BaselineScale);
                            structureTimer.Stop();
                            structureMilliseconds = structureTimer.Elapsed.TotalMilliseconds;
                            if (corrected is not null
                                && corrected.Result.OverlayTransform is { } correctedTransform)
                            {
                                var adaptiveStructure = EvaluateAdaptiveStructure(
                                    context,
                                    frame,
                                    corrected,
                                    now);
                                corrected = adaptiveStructure.Recognition;
                                correctedTransform = corrected.Result.OverlayTransform!;
                                tracker.Reanchor(
                                    frame.Image,
                                    frame.ViewportBounds,
                                    correctedTransform);
                                currentRecognition = corrected;
                                weakFrames = 0;
                                stableFrames = 0;
                                EnqueueOrbTrackingCommit(
                                    context,
                                    corrected,
                                    config.MaximumBaselineScaleChangeRatio);
                                if (adaptiveStructure.BecameReliable)
                                {
                                    PublishAdaptiveReliableStatus(
                                        context,
                                        frame,
                                        corrected);
                                }
                            }
                            else
                            {
                                NotifyAdaptiveStructureFailure(context);
                            }
                        }

                        if (ElapsedMilliseconds(lastMetricsLog) >= 5000)
                        {
                            lastMetricsLog = Stopwatch.GetTimestamp();
                            LogOrbMetrics(
                                context,
                                observation.Accepted ? "accepted" : "rejected",
                                captureTimer.Elapsed.TotalMilliseconds,
                                orbTimer.Elapsed.TotalMilliseconds,
                                structureMilliseconds,
                                weakFrames,
                                observation.RejectionReason,
                                observation);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.OrbTracking,
                MapLogLevel.Warning,
                "ORB tracking stopped after an unexpected failure.",
                details: new()
                {
                    ["generation"] = context.Generation,
                    ["exception"] = exception.ToString()
                });
        }
        finally
        {
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

    private RuntimeMapRecognition? TryCorrectOrbTrackingWithStructure(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition predicted,
        double maximumBaselineScaleChangeRatio,
        double baselineScale)
    {
        if (predicted.Result.OverlayTransform is not { } transform
            || _settings is null)
        {
            return null;
        }
        if (ProbeAdaptiveScaleStructure(context, frame, predicted) is not { } corrected
            || corrected.Result.OverlayTransform is not { } correctedTransform)
        {
            return null;
        }
        var correctedScale = (correctedTransform.ScaleX + correctedTransform.ScaleY) / 2d;
        if (!double.IsFinite(correctedScale)
            || baselineScale <= 0
            || Math.Abs((correctedScale / baselineScale) - 1d)
                > Math.Max(
                    0,
                    IsAdaptiveScaleEnabled
                        ? 0.50d
                        : maximumBaselineScaleChangeRatio))
        {
            return null;
        }
        return corrected;
    }

    private void EnqueueOrbTrackingCommit(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        double maximumBaselineScaleChangeRatio)
    {
        Volatile.Write(ref _pendingOrbTrackingRecognition, recognition);
        if (Interlocked.CompareExchange(ref _orbCommitQueued, 1, 0) != 0)
            return;

        void DrainPendingCommits()
        {
            try
            {
                while (Interlocked.Exchange(ref _pendingOrbTrackingRecognition, null) is { } pending)
                {
                    ApplyOrbTrackingCommit(context, pending, maximumBaselineScaleChangeRatio);
                }
            }
            finally
            {
                Volatile.Write(ref _orbCommitQueued, 0);
                if (Volatile.Read(ref _pendingOrbTrackingRecognition) is not null
                    && Interlocked.CompareExchange(ref _orbCommitQueued, 1, 0) == 0)
                {
                    _dispatcher.TryEnqueue(DrainPendingCommits);
                }
            }
        }

        if (!_dispatcher.TryEnqueue(DrainPendingCommits))
        {
            Volatile.Write(ref _orbCommitQueued, 0);
        }
    }

    private void ApplyOrbTrackingCommit(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        double maximumBaselineScaleChangeRatio)
    {
        try
        {
            if (!IsOrbTrackingContextCurrent(context) || recognition.Result.OverlayTransform is not { } transform || _lastAlignmentSession is not { } session)
                return;
            var effectiveScaleLimit = IsAdaptiveTransformConfirmed(context, transform) ? 0.50d : maximumBaselineScaleChangeRatio;
            var advanced = session.Advance(recognition.Map, recognition.Result, effectiveScaleLimit);
            _lastRecognition = recognition;
            _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);
            _lastAlignmentSession = advanced;
            if (CanUseAdaptiveReliableSession(advanced, context.AdaptiveKey))
                RememberPrimaryFloorSession(recognition, advanced);
            _alignmentTrackingMode = recognition.Result.Source switch
            {
                MapRecognitionSource.VpsgTracking => MapAlignmentTrackingMode.VpsgTracking,
                MapRecognitionSource.OrbTracking => MapAlignmentTrackingMode.OrbTracking,
                _ => MapAlignmentTrackingMode.StructureMatched
            };
            var renderTimer = Stopwatch.StartNew();
            _overlay.UpdateMapTransform(transform, preservePlayer: true);
            renderTimer.Stop();
            var previousRenderLog = Volatile.Read(ref _lastOrbRenderMetricsTimestamp);
            if (ElapsedMilliseconds(previousRenderLog) >= 5000)
            {
                Volatile.Write(ref _lastOrbRenderMetricsTimestamp, Stopwatch.GetTimestamp());
                _logCollector.Append(MapLogCategory.OrbTracking, MapLogLevel.Info, "ORB tracking render sample",
                    elapsedMs: renderTimer.Elapsed.TotalMilliseconds,
                    details: new() { ["generation"] = context.Generation, ["renderMs"] = renderTimer.Elapsed.TotalMilliseconds, ["overlayVisible"] = _overlay.IsVisible });
            }
        }
        catch (InvalidOperationException exception)
        {
            _logCollector.Append(MapLogCategory.OrbTracking, MapLogLevel.Warning,
                "A continuous tracking observation was rejected by the alignment session.",
                details: new() { ["generation"] = context.Generation, ["failureReason"] = exception.Message });
        }
    }

    private bool IsOrbTrackingContextCurrent(OrbTrackingContext context)
    {
        if (!EnsureOverlayCaptureExclusion("ORB tracking stopped because Overlay capture protection is no longer active.", "直播模式已关闭显示层保护。"))
            return false;
        if (_disposed
            || context.Generation != Volatile.Read(ref _orbTrackingGeneration)
            || !IsCurrentMatchOperation(context.Match)
            || !_gameMapToggleState.IsCurrent(context.Toggle))
        {
            return false;
        }
        var recognition = _lastRecognition;
        return recognition is not null
            && recognition.Map.Id == context.MapId
            && recognition.Map.UpdatedAt == context.MapUpdatedAt
            && string.Equals(recognition.Result.Floor, context.FloorKey, StringComparison.Ordinal);
    }

    private bool EnsureOverlayCaptureExclusion(string warningMessage, string defaultFailureReason)
    {
        // 仅当宿主请求了显示层保护（例如直播模式开启了隐藏显示层）时，才要求排除生效
        if (_captureProtection is { IsPluginEnabled: true })
        {
            if (_captureProtection.IsProtectionRequested(CaptureProtectionWindowCategory.DisplayLayer))
            {
                if (_overlay.IsCaptureExclusionEnabled || _overlay.TrySetCaptureExclusion(true, out var reason))
                {
                    Interlocked.Exchange(ref _orbCaptureExclusionWarningLogged, 0);
                    return true;
                }

                if (Interlocked.Exchange(ref _orbCaptureExclusionWarningLogged, 1) == 0)
                {
                    _logCollector.Append(MapLogCategory.OrbTracking, MapLogLevel.Warning, warningMessage,
                        details: new() { ["failureReason"] = string.IsNullOrWhiteSpace(reason) ? defaultFailureReason : reason });
                }
                return false;
            }

            // 直播模式开启但用户明确关闭了“隐藏显示层”：允许捕获，不阻止跟踪
            return true;
        }

        // 直播模式未启用（普通/录屏模式）：尊重全局捕获策略，不排除捕获，不阻止跟踪
        return true;
    }
}
// SessionOrchestrator.OrbTracking: Features/Maps partial module for continuous alignment and tracking orchestration.
