using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // A preview owns no identity lock, alignment session, lease or learned scale.
    private RuntimeMapRecognition? _provisionalRecognition;
    private object? _provisionalCatalogRevision;
    private readonly ScanObservationFrameCache _observationFrameCache = new();
    private readonly MapObservationPresentation _observationPresentation = new();
    private CancellationTokenSource? _observationCancellation;
    private Task _observationTask = Task.CompletedTask;
    private long _observationGeneration;
    private long _observationNextAttemptAt;

    public async Task SetContinuousObservationEnabledAsync(bool enabled)
    {
        if (_settings is null)
            throw new InvalidOperationException("设置尚未加载。");
        if (_settings.ContinuousObservationEnabled == enabled)
            return;
        _settings.ContinuousObservationEnabled = enabled;
        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            _settings.ContinuousObservationEnabled = !enabled;
            throw;
        }
        if (enabled)
            StartMapObservation();
        else
        {
            CancelMapObservation(clearPreview: true);
            _overlayStatus.Clear();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanObserveMap => !_disposed && _initialized && !_headless
        && _settings is { IsEnabled: true, ContinuousObservationEnabled: true,
            SelectMapByTagsEnabled: false, BackgroundScanEnabled: false }
        && !_silentScanActive && !_manualSelectionActive
        && Volatile.Read(ref _activeScanOperations) == 0
        && !_calibrationInput.IsActive
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
        _overlayStatus.Clear();
        // The completed quick scan's frame/deadline must not flow into future observations.
        using (ScanExecutionContext.Suppress())
            _observationTask = ObserveMapAsync(previous, scope, match, toggle, generation, delayFirstPass);
    }

    private bool IsMapObservationCurrent(MapMatchSnapshot match, MapGameToggleTransition toggle,
        long generation) => !_disposed && _settings is { IsEnabled: true, ContinuousObservationEnabled: true }
        && !_calibrationInput.IsActive && IsCurrentMatchOperation(match)
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
                var remaining = _observationNextAttemptAt - Environment.TickCount64;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
                if (!CanObserveMap || !IsMapObservationCurrent(match, toggle, generation)) break;
                var passStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try { await ObserveMapOnceAsync(match, toggle, generation, token); }
                finally
                {
                    // Closing and immediately reopening must not bypass the idle
                    // interval or start a burst of cancelled full-class scans.
                    var idle = Math.Clamp((int)Math.Ceiling(
                        System.Diagnostics.Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds * 3), 650, 3000);
                    _observationNextAttemptAt = Environment.TickCount64 + idle;
                }
                if (_lastRecognition is not null) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                "持续观察已停止", details: new() { ["exception"] = ex.ToString() });
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
            if (frame is not null && execution.CatalogRevision is { } revision
                && _observationFrameCache.Matches(frame, revision, mode))
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
            {
                PublishMapObservation(result, frame, match, pass.Token);
            }
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
        }
        // A fully compared negative result is reusable too. Repeating identical
        // evidence cannot resolve an identity, even when there is no preview.
        // Incomplete retrieval / timeout / unverified poses are never cached.
        if (execution.RetrievalCompleted && result.PendingSideEntranceScan is { } scan
            && scan.Candidates.Count > 0 && scan.Candidates.Count == scan.EligibleMapCount
            && scan.Candidates.All(c => c.IdentityEvidence.State != ScanIdentityState.Unverified))
            _observationFrameCache.Remember(frame, execution.CatalogRevision, execution.Policy.Mode);
        if (usable) RefreshMiniMapForCurrentFloor();
        _overlay.Show();
        _logCollector.Append(MapLogCategory.Overlay, MapLogLevel.Info,
            $"暂显发布 · action={(usable ? "replace" : "retain")} · hasMap={_overlay.HasMap}",
            details: new() { ["mapId"] = _provisionalRecognition?.Map.Id });
        StateChanged?.Invoke(this, EventArgs.Empty);
        return usable;
    }
}
