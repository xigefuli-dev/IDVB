using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void StartReferencePythonTracking(RuntimeMapRecognition recognition, CapturedGameFrame seedFrame)
    {
        if (!_recognition.UsesReferencePython(recognition.Map)
            || recognition.EntryCatalogRevision is not { } revision
            || recognition.Result.OverlayTransform is not { } transform
            || !ReferenceEquals(_lastRecognition, recognition)
            || !_gameMapToggleState.IsOpen || !_matchSession.Snapshot.IsStarted
            || _settings is not { IsEnabled: true })
            return;
        if (!EnsureOverlayCaptureExclusion("Python 跟踪停止：显示层捕获保护不可用。", "捕获排除不可用。"))
            return;
        // The general capture policy may permit unprotected recording. That
        // does not mean the visible map overlay has been excluded from pixels.
        if (!_overlay.IsCaptureExclusionEnabled && !_overlay.TrySetCaptureExclusion(true, out var reason))
        {
            _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                "Python 跟踪未启动：无法从截图中排除显示层。", details: new() { ["reason"] = reason });
            return;
        }
        var floors = MapFloorRules.GetOrderedFloors(recognition.Map).Select(floor => floor.Key).ToArray();
        var group = FloorIndicatorTemplateRegistry.Resolve(floors);
        if (group is null)
        {
            _logCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Warning,
                "Python 跟踪未启动：目标地图缺少楼层指示器模板。");
            return;
        }

        var context = new OrbTrackingContext(Interlocked.Increment(ref _orbTrackingGeneration),
            _matchSession.Snapshot, new MapGameToggleTransition(true, _gameMapToggleState.Version),
            recognition.Map.Id, recognition.Map.UpdatedAt, recognition.Result.Floor,
            CreateAdaptiveScaleKey(seedFrame, recognition.Map, recognition.Result.Floor),
            (transform.ScaleX + transform.ScaleY) / 2d);
        var scope = CancellationTokenSource.CreateLinkedTokenSource(CurrentMatchCancellationToken, _lifetimeCts.Token);
        var token = scope.Token;
        var windowHandle = seedFrame.WindowHandle;
        var config = _config.Get<OrbTrackingConfig>("orb_tracking");
        lock (_orbTrackingGate)
        {
            _orbTrackingCancellation = scope;
            using var suppressScan = ScanExecutionContext.Suppress();
            _orbTrackingTask = Task.Run(() => RunReferencePythonTrackingLoopAsync(context, recognition,
                revision, windowHandle, new AutoFloorCapture(recognition.Map, group), config, token));
        }
        _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            $"Python 持续贴合已启动 · map={context.MapId} · floor={context.FloorKey}");
    }

    private bool IsReferencePythonTrackingCurrent(OrbTrackingContext context, MapCatalogRevision revision) =>
        IsOrbTrackingContextCurrent(context) && _settings is { IsEnabled: true }
        && string.Equals(_currentFloorKey, context.FloorKey, StringComparison.Ordinal)
        && revision.Equals(_recognition.CatalogRevision) && revision.Equals(_mapRepository.GetCatalogRevision())
        && _recognition.TryGetMap(context.MapId)?.UpdatedAt == context.MapUpdatedAt;

    private async Task RunReferencePythonTrackingLoopAsync(OrbTrackingContext context,
        RuntimeMapRecognition recognition, MapCatalogRevision revision, IntPtr windowHandle,
        AutoFloorCapture autoFloor, OrbTrackingConfig config, CancellationToken token)
    {
        var current = recognition;
        var weakFrames = 0;
        var fullClient = new NormalizedRectangle { X = 0, Y = 0, Width = 1, Height = 1 };
        string? changedFloor = null;
        MapOperationTraceAmbient.SetCurrent(null);
        try
        {
            while (!token.IsCancellationRequested && IsReferencePythonTrackingCurrent(context, revision))
            {
                await Task.Delay(Math.Max(20, config.ActiveIntervalMs), token).ConfigureAwait(false);
                if (!IsReferencePythonTrackingCurrent(context, revision)) break;
                // Open/scan owns this same gate. A tracker never queues ahead
                // of a foreground operation or starts a parallel solver.
                if (!await _scanGate.WaitAsync(0, token).ConfigureAwait(false)) continue;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsReferencePythonTrackingCurrent(context, revision)) break;
                    if (!_overlay.IsCaptureExclusionEnabled)
                        throw new InvalidOperationException("Python 跟踪截图的显示层排除已失效。");
                    var viewport = ResolveMapViewportForCurrentWindow();
                    if (!_captureSvc.TryCaptureViewport(fullClient, out var capturedObject, out _)
                        || capturedObject is not CapturedGameFrame captured)
                    {
                        await ObserveReferencePythonWeakFrameAsync(context, revision, ++weakFrames).ConfigureAwait(false);
                        continue;
                    }
                    if (captured.WindowHandle != windowHandle || !IsCurrentCaptureTarget(captured))
                    {
                        captured.Dispose();
                        await ObserveReferencePythonWeakFrameAsync(context, revision, ++weakFrames).ConfigureAwait(false);
                        continue;
                    }
                    // Extract owns/disposes the full capture, retaining both
                    // the viewport ROI and its original client pixel buffer.
                    using var frame = autoFloor.Extract(captured, viewport);
                    if (frame.FullClientImage is null)
                        throw new InvalidDataException("Python 跟踪未取得完整客户区原始像素。");
                    var detected = MapScanFloorRules.NormalizeFloorIdentity(frame.DetectedFloorKey);
                    var expected = MapScanFloorRules.NormalizeFloorIdentity(context.FloorKey);
                    if (frame.DetectedFloorKey is not null && detected != expected)
                    {
                        changedFloor = frame.DetectedFloorKey;
                        break;
                    }
                    if (frame.DetectedFloorKey is null && _settings?.DisableAutoFloor != true)
                    {
                        await ObserveReferencePythonWeakFrameAsync(context, revision, ++weakFrames).ConfigureAwait(false);
                        continue;
                    }

                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(1000);
                    MapRecognitionAttempt attempt;
                    try
                    {
                        var prior = CreateReferencePythonPrior(context.Match, frame, current.Map, context.FloorKey);
                        attempt = await _recognition.AlignReferencePythonAsync(frame, current.Map,
                            context.FloorKey, prior, 1000, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        await ObserveReferencePythonWeakFrameAsync(context, revision, ++weakFrames).ConfigureAwait(false);
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    if (attempt.Recognition is not { ReferencePythonValidated: true } aligned)
                    {
                        await ObserveReferencePythonWeakFrameAsync(context, revision, ++weakFrames).ConfigureAwait(false);
                        continue;
                    }
                    var published = await DispatchReferencePythonTrackingAsync(async () =>
                    {
                        if (token.IsCancellationRequested || !IsReferencePythonTrackingCurrent(context, revision)
                            || !IsCurrentCaptureTarget(frame) || frame.WindowHandle != windowHandle
                            || aligned.Map.Id != context.MapId || aligned.Map.UpdatedAt != context.MapUpdatedAt
                            || aligned.Result.Floor != context.FloorKey || aligned.EntryCatalogRevision != revision)
                            return false;
                        var operation = CaptureMapOpenOperationContext(context.Toggle, context.Match, current,
                            token, context.FloorKey, frame.WindowHandle, frame.ClientBounds);
                        _lastDiagnostics = attempt.Diagnostics;
                        var outcome = await PublishMapOpenAlignmentResultAsync(context.Toggle, context.Match,
                            frame, current, context.FloorKey, recoveringSelectedIdentity: false, aligned,
                            failureReason: null, repairCacheKey: null, resetRecoveredScaleState: false,
                            context: operation, trackingUpdate: true);
                        if (outcome == MapOpenAlignmentPublishOutcome.Succeeded)
                        {
                            _alignmentTrackingMode = MapAlignmentTrackingMode.StructureMatched;
                            if (weakFrames > 0) StateChanged?.Invoke(this, EventArgs.Empty);
                        }
                        return outcome == MapOpenAlignmentPublishOutcome.Succeeded;
                    }).ConfigureAwait(false);
                    if (!published) break;
                    current = aligned;
                    weakFrames = 0;
                }
                finally { _scanGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                "Python 持续贴合已停止，等待下一次开图。", details: new() { ["exception"] = exception.ToString() });
            await ObserveReferencePythonWeakFrameAsync(context, revision, 30).ConfigureAwait(false);
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
            if (changedFloor is not null && !token.IsCancellationRequested)
                QueueReferencePythonFloorReopen(context, revision, changedFloor);
        }
    }

    private Task<bool> ObserveReferencePythonWeakFrameAsync(OrbTrackingContext context,
        MapCatalogRevision revision, int weakFrames)
    {
        // Preserve the existing VPSG holding/lost observation thresholds.
        if (weakFrames is not (5 or 30)) return Task.FromResult(false);
        return DispatchReferencePythonTrackingAsync(() =>
        {
            if (!IsReferencePythonTrackingCurrent(context, revision)) return Task.FromResult(false);
            _alignmentTrackingMode = weakFrames >= 30
                ? MapAlignmentTrackingMode.Lost : MapAlignmentTrackingMode.HoldingLastTransform;
            if (weakFrames >= 30)
            {
                _overlay.SetMainContentVisible(false);
                _statusMessage = "本帧贴合证据持续不足；地图身份已保留，等待重新确认。";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            return Task.FromResult(true);
        });
    }

    private void QueueReferencePythonFloorReopen(OrbTrackingContext context,
        MapCatalogRevision revision, string detectedFloor)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!IsReferencePythonTrackingCurrent(context, revision)) return;
            _overlay.SetMainContentVisible(false);
            _statusMessage = $"检测到楼层变化：{detectedFloor}；等待该楼层重新贴合。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (_settings?.DisableAutoFloor == true) return;
            // The old loop has released its scan gate and detached its task.
            // Scheduling, instead of awaiting, avoids draining this very task.
            StartInputOperation("python-floor-realignment", () => RunMapOpenAlignmentAsync(context.Toggle));
        });
    }

    private Task<bool> DispatchReferencePythonTrackingAsync(Func<Task<bool>> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }))
            completion.TrySetResult(false);
        // Once queued, the callback owns the frame until it finishes. Returning
        // on caller cancellation here would dispose a Mat still used by the UI.
        return completion.Task;
    }
}
