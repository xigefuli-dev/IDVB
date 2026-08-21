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

    private sealed record OrbTrackingContext(
        long Generation,
        MapMatchSnapshot Match,
        MapGameToggleTransition Toggle,
        Guid MapId,
        DateTimeOffset MapUpdatedAt,
        string FloorKey,
        AdaptiveScaleKey AdaptiveKey,
        double BaselineScale);

    private async Task StartOrbTrackingAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame seedFrame)
    {
        var config = _config.Get<OrbTrackingConfig>("orb_tracking");
        CancelOrbTracking("alignment replaced");
        await DrainOrbTrackingAsync();
        if ((!config.Enabled && !IsAdaptiveScaleEnabled)
            || recognition.Result.OverlayTransform is not { } transform
            || !_gameMapToggleState.IsOpen
            || !_matchSession.Snapshot.IsStarted)
        {
            return;
        }

        if (!_overlay.IsCaptureExclusionEnabled)
        {
            if (Interlocked.Exchange(ref _orbCaptureExclusionWarningLogged, 1) == 0)
            {
                _logCollector.Append(
                    MapLogCategory.OrbTracking,
                    MapLogLevel.Warning,
                    "ORB tracking disabled because the overlay cannot be excluded from capture.",
                    details: new()
                    {
                        ["failureReason"] = "直播模式未实际应用 Overlay 捕获保护。"
                    });
            }
            return;
        }

        var generation = Interlocked.Increment(ref _orbTrackingGeneration);
        var context = new OrbTrackingContext(
            generation,
            _matchSession.Snapshot,
            new MapGameToggleTransition(true, _gameMapToggleState.Version),
            recognition.Map.Id,
            recognition.Map.UpdatedAt,
            recognition.Result.Floor,
            CreateAdaptiveScaleKey(
                seedFrame,
                recognition.Map,
                recognition.Result.Floor),
            (transform.ScaleX + transform.ScaleY) / 2d);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            CurrentMatchCancellationToken,
            _lifetimeCts.Token);
        var seed = config.Enabled ? seedFrame.Image.Clone() : null;
        var viewportBounds = seedFrame.ViewportBounds;
        lock (_orbTrackingGate)
        {
            _orbTrackingCancellation = linked;
            _orbTrackingTask = config.Enabled
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
            config.Enabled ? MapLogCategory.OrbTracking : MapLogCategory.StructureRegistration,
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
                var effectiveScaleLimit = IsAdaptiveTransformConfirmed(context, transform)
                    ? 0.50d
                    : maximumBaselineScaleChangeRatio;
                var advanced = session.Advance(
                    recognition.Map,
                    recognition.Result,
                    effectiveScaleLimit);
                _lastRecognition = recognition;
                _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);
                _lastAlignmentSession = advanced;
                if (CanUseAdaptiveReliableSession(advanced, context.AdaptiveKey))
                {
                    RememberPrimaryFloorSession(recognition, advanced);
                }
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
        if (!_overlay.IsCaptureExclusionEnabled)
        {
            if (Interlocked.Exchange(ref _orbCaptureExclusionWarningLogged, 1) == 0)
            {
                _logCollector.Append(
                    MapLogCategory.OrbTracking,
                    MapLogLevel.Warning,
                    "ORB tracking stopped because Overlay capture protection is no longer active.",
                    details: new()
                    {
                        ["failureReason"] = "直播模式已关闭显示层保护。"
                    });
            }
            return false;
        }
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
/*
 * 文件职责：SessionOrchestrator.OrbTracking。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
