using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // A preview owns no identity lock, alignment session, lease or learned scale.
    private RuntimeMapRecognition? _provisionalRecognition;
    private object? _provisionalCatalogRevision;
    private readonly ScanObservationFrameCache _observationFrameCache = new();
    private readonly MapObservationPresentation _observationPresentation = new();
    private ScanPerformanceMode _observationFrameMode;
    private CancellationTokenSource? _observationCancellation;
    private Task _observationTask = Task.CompletedTask;
    private long _observationGeneration;

    private bool CanObserveMap => !_disposed && _initialized && !_headless
        && _settings is { IsEnabled: true, SelectMapByTagsEnabled: false, BackgroundScanEnabled: false }
        && !_silentScanActive && !_manualSelectionActive
        && _lastRecognition is null && _pendingAlignmentIdentity is null
        && _matchSession.Snapshot.IsStarted && _matchSession.Snapshot.Mode != MapRunMode.Survey && !IsMatchEnding;

    private void CancelMapObservation(bool clearPreview = false, bool hideRegion = true)
    {
        Interlocked.Increment(ref _observationGeneration);
        _observationCancellation?.Cancel();
        _observationFrameCache.Reset();
        if (hideRegion)
        {
            _observationPresentation.Reset();
            _overlay.SetObservationRegion(null);
        }
        if (!clearPreview) return;
        if (_provisionalRecognition is not null && _lastRecognition is null && _pendingAlignmentIdentity is null)
        {
            _overlay.ClearMap();
            _overlay.ClearPersistentMiniMap();
            _overlayStatus.Clear();
        }
        _provisionalRecognition = null;
        _provisionalCatalogRevision = null;
    }

    private void StartMapObservation(bool delayFirstPass = false)
    {
        if (!CanObserveMap || !_gameMapToggleState.IsOpen) return;
        CancelMapObservation(hideRegion: false);
        var previous = _observationTask;
        var scope = CancellationTokenSource.CreateLinkedTokenSource(CurrentMatchCancellationToken);
        _observationCancellation = scope;
        var match = _matchSession.Snapshot;
        var toggle = new MapGameToggleTransition(true, _gameMapToggleState.Version);
        var generation = Volatile.Read(ref _observationGeneration);
        // The completed quick scan's frame/deadline must not flow into future observations.
        using (ScanExecutionContext.Suppress())
            _observationTask = ObserveMapAsync(previous, scope, match, toggle, generation, delayFirstPass);
    }

    private bool IsMapObservationCurrent(MapMatchSnapshot match, MapGameToggleTransition toggle,
        long generation) => !_disposed && _settings?.IsEnabled == true && IsCurrentMatchOperation(match)
        && _gameMapToggleState.IsCurrent(toggle)
        && generation == Volatile.Read(ref _observationGeneration);

    private async Task ObserveMapAsync(Task previous, CancellationTokenSource scope,
        MapMatchSnapshot match, MapGameToggleTransition toggle, long generation, bool delayFirstPass)
    {
        var token = scope.Token;
        MapOperationTraceAmbient.SetCurrent(null);
        try
        {
            await Task.Delay(delayFirstPass ? 650 : 1, token);
            await previous;
            while (!token.IsCancellationRequested && CanObserveMap && IsMapObservationCurrent(match, toggle, generation))
            {
                await ObserveMapOnceAsync(match, toggle, generation, token);
                if (_lastRecognition is not null) break;
                // Every observation has its own bounded budget; waiting does not grow confidence.
                await Task.Delay(650, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                "持续观察已停止", details: new() { ["exception"] = ex.ToString() });
            if (IsMapObservationCurrent(match, toggle, generation))
            {
                _statusMessage = "观察暂时中断，下次开图将重试。";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (ReferenceEquals(_observationCancellation, scope)) _observationCancellation = null;
            scope.Dispose();
        }
    }

    private async Task ObserveMapOnceAsync(MapMatchSnapshot match, MapGameToggleTransition toggle,
        long generation, CancellationToken token)
    {
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(token);
        var mode = _settings!.ScanPerformanceMode;
        pass.CancelAfter(ScanExecutionPolicy.For(mode).BudgetMilliseconds);
        using var execution = ScanExecutionContext.Enter(mode, pass.Token,
            () => IsMapObservationCurrent(match, toggle, generation));
        using var suppressDiagnostics = MapDiagnosticModeCapture.Suppress();
        var acquired = false;
        try
        {
            acquired = await _scanGate.WaitAsync(Math.Max(0, execution.RemainingMilliseconds - 60), pass.Token);
            if (!acquired || !execution.CanCompute) return;
            await EnsureMapCacheSynchronizedAsync().WaitAsync(
                TimeSpan.FromMilliseconds(Math.Max(1, execution.RemainingMilliseconds - 60)), pass.Token);
            if (!execution.CanCompute) return;
            if (_provisionalCatalogRevision?.Equals(_recognition.CatalogRevision) == false)
            {
                _provisionalRecognition = null;
                _provisionalCatalogRevision = null;
                _observationFrameCache.Reset();
                _observationPresentation.Reset();
                _overlay.SetObservationRegion(null);
                _overlay.ClearMap();
                RefreshMiniMapForCurrentFloor();
            }
            execution.CatalogRevision = _recognition.CatalogRevision;
            execution.VariantGroups = _recognition.ScanVariantGroups;
            if (!_captureSvc.TryGetForegroundClientBounds(out _, out _, out _))
            {
                _overlay.Hide();
                return;
            }

            using var frame = await CaptureStableViewportAsync("持续观察", pass.Token,
                shouldContinue: () => IsMapObservationCurrent(match, toggle, generation), allowPartialMap: true,
                suspendOverlayForCapture: viewport => SuspendMapObservationCapture(viewport,
                    () => IsMapObservationCurrent(match, toggle, generation)));
            if (!IsMapObservationCurrent(match, toggle, generation)) return;
            if (frame is not null)
                _observationPresentation.RetainForTarget(_overlay, frame.ClientBounds,
                    frame.ViewportBounds, frame.WindowHandle, frame.DetectedFloorKey);
            if (frame is not null && _provisionalRecognition is not null && mode == _observationFrameMode
                && _observationFrameCache.Matches(frame))
            {
                return;
            }
            _observationFrameCache.Reset();
            // Keep the displayed preview through changed pixels, incomplete comparisons
            // and timeouts. Only a usable replacement or a changed target revokes it.
            if (frame is null || !execution.CanCompute) return;

            var result = CreateObservationRecognitionState();
            await Task.Run(() => RunInitialSideEntranceRecognition(frame, result), pass.Token);
            if (!IsMapObservationCurrent(match, toggle, generation) || execution.Expired) return;
            if (!IsCurrentCaptureTarget(frame))
            {
                _overlay.Hide();
                return;
            }
            if (TryCommitAutomaticScan(result, frame, match, pass.Token))
            {
                if (!execution.Expired && result.Recognition is { } confirmed)
                    await StartOrbTrackingAsync(confirmed, frame);
            }
            else
                PublishMapObservation(result, frame, match, pass.Token);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (pass.IsCancellationRequested) { }
        finally
        {
            if (acquired) _scanGate.Release();
            FinishScanExecution(execution);
        }
    }

    private IDisposable SuspendMapObservationCapture(NormalizedRectangle viewport, Func<bool> isCurrent)
    {
        var hasTarget = _captureSvc.TryGetForegroundClientBounds(out var bounds, out var window, out _);
        if (hasTarget && bounds is MapScreenRect client)
            _observationPresentation.RetainForTarget(_overlay, client,
                DwrGameWindowCaptureService.GetViewportBounds(client, viewport), window);
        return MapObservationPresentation.SuspendForCapture(_overlay, () => isCurrent() && hasTarget
            && _captureSvc.TryGetForegroundClientBounds(out var currentBounds, out var currentWindow, out _)
            && Equals(bounds, currentBounds) && window == currentWindow);
    }

    private InitialRecognitionPipelineState CreateObservationRecognitionState() => new()
    {
        ObserveUntilConfirmed = true,
        PreviousPreviewMapId = _provisionalRecognition?.Map.Id,
        PreviousPreviewFloor = _provisionalRecognition?.Result.Floor
    };

    private bool IsCurrentCaptureTarget(CapturedGameFrame frame) =>
        _captureSvc.TryGetForegroundClientBounds(out var bounds, out var window, out _)
        && bounds is MapScreenRect current && current == frame.ClientBounds && window == frame.WindowHandle;

    private bool PublishMapObservation(InitialRecognitionPipelineState result, CapturedGameFrame frame,
        MapMatchSnapshot match, CancellationToken token)
    {
        var execution = ScanExecutionContext.Current;
        if (execution is null || execution.Expired || token.IsCancellationRequested
            || !IsCurrentMatchOperation(match) || _lastRecognition is not null || _pendingAlignmentIdentity is not null
            || !IsCurrentCaptureTarget(frame)
            || execution.CatalogRevision?.Equals(_recognition.CatalogRevision) != true
            || execution.CatalogRevision.Equals(_mapRepository.GetCatalogRevision()) != true)
            return false;

        var preview = result.ProvisionalRecognition;
        var candidate = result.PendingSideEntranceScan?.Candidates.FirstOrDefault(c =>
            c.Map.Id == preview?.Map.Id && c.FloorKey == preview.Result.Floor);
        var usable = preview?.Result.OverlayTransform is { } transform
            && candidate is { Disposition: SideEntranceCandidateDisposition.Reliable,
                IdentityEvidence.State: ScanIdentityState.Supported }
            && ReferenceEquals(candidate.VerifiedTransform, transform)
            && ReferenceEquals(execution.Frame?.Source, frame.Image)
            && string.Equals(preview.Map.Class, match.MapClass, StringComparison.OrdinalIgnoreCase)
            && File.Exists(preview.FloorImagePath);
        using var present = _overlay.DeferPresent();
        _lastGameBounds = frame.ClientBounds;
        _lastGameWindowHandle = frame.WindowHandle;
        _observationPresentation.Publish(_overlay, usable ? preview : null, frame, _settings!.ShowOverlayStatus);
        if (usable)
        {
            _provisionalRecognition = preview;
            _provisionalCatalogRevision = execution.CatalogRevision;
            if (execution.RetrievalCompleted && result.PendingSideEntranceScan is { } scan
                && scan.Candidates.Count == scan.EligibleMapCount
                && scan.Candidates.All(c => c.IdentityEvidence.State != ScanIdentityState.Unverified))
            {
                _observationFrameCache.Remember(frame);
                _observationFrameMode = execution.Policy.Mode;
            }
        }
        RefreshMiniMapForCurrentFloor();
        _statusMessage = usable ? $"暂显 {preview!.Map.DisplayName} · 正在确认"
            : _provisionalRecognition is not null ? "保留暂显资源 · 等待新的可见结构" : "正在观察，等待可见结构";
        _overlayStatus.Show(new MapOverlayStatus(MapOverlayStatusLevel.Scanning,
            "正在确认地图", _statusMessage), frame.ClientBounds, frame.WindowHandle, _settings!.ShowOverlayStatus, transient: false);
        _overlay.Show();
        _logCollector.Append(MapLogCategory.Overlay, MapLogLevel.Info,
            $"暂显发布 · action={(usable ? "replace" : "retain")} · hasMap={_overlay.HasMap}",
            details: new() { ["mapId"] = _provisionalRecognition?.Map.Id });
        StateChanged?.Invoke(this, EventArgs.Empty);
        return usable;
    }
}
