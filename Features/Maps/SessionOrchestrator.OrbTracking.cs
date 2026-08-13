using IDVBuff.Core.Models;
using OpenCvSharp;
using System.Diagnostics;

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

    private sealed record OrbTrackingContext(
        long Generation,
        MapMatchSnapshot Match,
        MapGameToggleTransition Toggle,
        Guid MapId,
        DateTimeOffset MapUpdatedAt,
        string FloorKey,
        double BaselineScale);

    private async Task StartOrbTrackingAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame seedFrame)
    {
        var config = _config.Get<OrbTrackingConfig>("orb_tracking");
        CancelOrbTracking("alignment replaced");
        await DrainOrbTrackingAsync();
        if (!config.Enabled
            || recognition.Result.OverlayTransform is not { } transform
            || !_gameMapToggleState.IsOpen
            || !_matchSession.Snapshot.IsStarted)
        {
            return;
        }

        if (!_overlay.TryEnableCaptureExclusion(out var exclusionFailure))
        {
            if (Interlocked.Exchange(ref _orbCaptureExclusionWarningLogged, 1) == 0)
            {
                _logCollector.Append(
                    MapLogCategory.OrbTracking,
                    MapLogLevel.Warning,
                    "ORB tracking disabled because the overlay cannot be excluded from capture.",
                    details: new()
                    {
                        ["failureReason"] = exclusionFailure
                    });
            }
            return;
        }

        var seed = seedFrame.Image.Clone();
        var viewportBounds = seedFrame.ViewportBounds;
        var generation = Interlocked.Increment(ref _orbTrackingGeneration);
        var context = new OrbTrackingContext(
            generation,
            _matchSession.Snapshot,
            new MapGameToggleTransition(true, _gameMapToggleState.Version),
            recognition.Map.Id,
            recognition.Map.UpdatedAt,
            recognition.Result.Floor,
            (transform.ScaleX + transform.ScaleY) / 2d);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            CurrentMatchCancellationToken,
            _lifetimeCts.Token);
        lock (_orbTrackingGate)
        {
            _orbTrackingCancellation = linked;
            _orbTrackingTask = Task.Run(
                () => RunOrbTrackingLoopAsync(
                    context,
                    recognition,
                    seed,
                    viewportBounds,
                    transform,
                    config,
                    linked.Token));
        }
        _logCollector.Append(
            MapLogCategory.OrbTracking,
            MapLogLevel.Info,
            $"ORB tracking started · map={context.MapId} · floor={context.FloorKey} · generation={generation}");
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
                            currentRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                currentRecognition,
                                tracker.CurrentTransform,
                                MapRecognitionSource.OrbTracking);
                            if (observation.ShouldCommit)
                            {
                                EnqueueOrbTrackingCommit(
                                    context,
                                    MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                        currentRecognition,
                                        observation.Transform,
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
                        var structureMilliseconds = 0d;
                        if (ElapsedMilliseconds(lastStructureCorrection) >= correctionInterval)
                        {
                            lastStructureCorrection = Stopwatch.GetTimestamp();
                            var structureTimer = Stopwatch.StartNew();
                            var predictedRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
                                currentRecognition,
                                tracker.CurrentTransform,
                                MapRecognitionSource.OrbTracking);
                            var corrected = TryCorrectOrbTrackingWithStructure(
                                frame,
                                predictedRecognition,
                                config.MaximumBaselineScaleChangeRatio,
                                context.BaselineScale);
                            structureTimer.Stop();
                            structureMilliseconds = structureTimer.Elapsed.TotalMilliseconds;
                            if (corrected is not null
                                && corrected.Result.OverlayTransform is { } correctedTransform)
                            {
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
        var lockedSession = MapAlignmentSession.FromRecognition(
            predicted.Map,
            predicted.Result);
        var attempt = AlignNoDoorLocalStructure(
            frame,
            predicted,
            predicted.Result.Floor,
            lockedSession,
            MapOverlayAlignmentMode.Uniform,
            _settings.RecognitionTuning.Clone(),
            CreateEffectiveStructureTuning(),
            [],
            predicted.Result.IdentityConfidence,
            allowTrackingScaleSearch: true);
        if (attempt.Recognition is not { } corrected
            || corrected.Result.OverlayTransform is not { } correctedTransform)
        {
            return null;
        }
        var correctedScale = (correctedTransform.ScaleX + correctedTransform.ScaleY) / 2d;
        if (!double.IsFinite(correctedScale)
            || baselineScale <= 0
            || Math.Abs((correctedScale / baselineScale) - 1d)
                > Math.Max(0, maximumBaselineScaleChangeRatio))
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
        if (Interlocked.Exchange(ref _orbCommitQueued, 1) != 0)
            return;
        if (_dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (!IsOrbTrackingContextCurrent(context)
                    || recognition.Result.OverlayTransform is not { } transform
                    || _lastAlignmentSession is not { } session)
                {
                    return;
                }
                var advanced = session.Advance(
                    recognition.Map,
                    recognition.Result,
                    maximumBaselineScaleChangeRatio);
                _lastRecognition = recognition;
                _lastAlignmentSession = advanced;
                RememberPrimaryFloorSession(recognition, advanced);
                _alignmentTrackingMode = recognition.Result.Source
                    == MapRecognitionSource.OrbTracking
                        ? MapAlignmentTrackingMode.OrbTracking
                        : MapAlignmentTrackingMode.StructureMatched;
                var renderTimer = Stopwatch.StartNew();
                _overlay.UpdateMapTransform(transform, preservePlayer: true);
                renderTimer.Stop();
                var previousRenderLog = Volatile.Read(ref _lastOrbRenderMetricsTimestamp);
                if (ElapsedMilliseconds(previousRenderLog) >= 5000)
                {
                    Volatile.Write(
                        ref _lastOrbRenderMetricsTimestamp,
                        Stopwatch.GetTimestamp());
                    _logCollector.Append(
                        MapLogCategory.OrbTracking,
                        MapLogLevel.Info,
                        "ORB tracking render sample",
                        elapsedMs: renderTimer.Elapsed.TotalMilliseconds,
                        details: new()
                        {
                            ["generation"] = context.Generation,
                            ["renderMs"] = renderTimer.Elapsed.TotalMilliseconds,
                            ["overlayVisible"] = _overlay.IsVisible
                        });
                }
            }
            catch (InvalidOperationException exception)
            {
                _logCollector.Append(
                    MapLogCategory.OrbTracking,
                    MapLogLevel.Warning,
                    "A continuous tracking observation was rejected by the alignment session.",
                    details: new()
                    {
                        ["generation"] = context.Generation,
                        ["failureReason"] = exception.Message
                    });
            }
            finally
            {
                Volatile.Write(ref _orbCommitQueued, 0);
            }
        }))
        {
            return;
        }
        Volatile.Write(ref _orbCommitQueued, 0);
    }

    private bool IsOrbTrackingContextCurrent(OrbTrackingContext context)
    {
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
            && string.Equals(
                recognition.Result.Floor,
                context.FloorKey,
                StringComparison.Ordinal);
    }

    private void CancelOrbTracking(string reason)
    {
        Interlocked.Increment(ref _orbTrackingGeneration);
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_orbTrackingGate)
        {
            cancellation = _orbTrackingCancellation;
            task = _orbTrackingTask;
            if (task is not null)
                _retiredOrbTrackingTask = task;
            _orbTrackingCancellation = null;
            _orbTrackingTask = null;
        }
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation?.Dispose();
        if (task is not null)
        {
            _logCollector.Append(
                MapLogCategory.OrbTracking,
                MapLogLevel.Info,
                $"ORB tracking invalidated · reason={reason}");
        }
    }

    private async Task DrainOrbTrackingAsync()
    {
        Task? task;
        Task? retired;
        lock (_orbTrackingGate)
        {
            task = _orbTrackingTask;
            retired = _retiredOrbTrackingTask;
        }
        if (task is null && retired is null)
            return;
        try
        {
            await Task.WhenAll(
                new[] { task, retired }.OfType<Task>());
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_orbTrackingGate)
            {
                if (_retiredOrbTrackingTask == retired)
                    _retiredOrbTrackingTask = null;
            }
        }
    }

}
