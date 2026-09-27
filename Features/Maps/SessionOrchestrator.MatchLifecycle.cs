using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly SemaphoreSlim _matchLifecycleGate = new(1, 1);
    private CancellationTokenSource? _matchCancellation;
    private readonly object _mapOpenCancellationGate = new();
    private CancellationTokenSource? _mapOpenCancellation;
    private int _matchEnding;
    private long _currentMapOpenGeneration;

    // A user-confirmed map remains useful evidence even when its first
    // alignment attempt fails. Keep that identity and its scan seed separate
    // from _lastRecognition so an unverified transform is never rendered.
    private RuntimeMapRecognition? _pendingAlignmentIdentity;
    private MapAlignmentSession? _pendingAlignmentSeed;

    private bool IsMatchEnding => Volatile.Read(ref _matchEnding) != 0;

    private CancellationToken CurrentMatchCancellationToken =>
        _matchCancellation?.Token ?? new CancellationToken(canceled: true);

    private bool IsCurrentMatchOperation(MapMatchSnapshot operationMatch) =>
        !IsMatchEnding && _matchSession.IsCurrent(operationMatch);

    private MapOpenOperationContext CaptureMapOpenOperationContext(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        RuntimeMapRecognition locked,
        CancellationToken cancellationToken,
        string? currentFloorKey = null,
        IntPtr windowHandle = default,
        MapScreenRect clientBounds = default)
    {
        var generation = Interlocked.Increment(ref _currentMapOpenGeneration);
        return new MapOpenOperationContext
        {
            OperationMatch = operationMatch,
            MapId = locked.Map.Id,
            MapUpdatedAt = locked.Map.UpdatedAt,
            MapToggleVersion = toggle.Version,
            OperationGeneration = generation,
            // A current/previous floor is fallback state, not a manual lock.
            ManualFloorKey = _settings?.DisableAutoFloor == true
                ? currentFloorKey ?? MapFloorRules.GetPrimaryFloorKey(locked.Map)
                : null,
            WindowHandle = windowHandle,
            ClientBounds = clientBounds,
            CancellationToken = cancellationToken
        };
    }

    private bool IsMapOpenOperationCurrent(MapOpenOperationContext context)
    {
        if (!IsCurrentMatchOperation(context.OperationMatch))
            return false;

        var currentLocked = _pendingAlignmentIdentity ?? _lastRecognition;
        return context.MatchesCurrentOperation(
            _matchSession.Snapshot,
            _gameMapToggleState.IsOpen,
            _gameMapToggleState.Version,
            Volatile.Read(ref _currentMapOpenGeneration),
            currentLocked?.Map.Id,
            currentLocked?.Map.UpdatedAt,
            _currentFloorKey);
    }

    private void InvalidateActiveMapOpenOperation(string reason = "operation-invalidated")
    {
        Interlocked.Increment(ref _currentMapOpenGeneration);
        CancelMapOpenAlignment();
        _alignmentCommitGuard.Invalidate();
    }

    private void StartMatchCancellationScope()
    {
        _matchCancellation?.Cancel();
        _matchCancellation?.Dispose();
        _matchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
        var nativeMiniMapToken = _matchCancellation.Token;
        _nativeMiniMapCaptureTask = Task.Run(() => RunNativeMiniMapCaptureAsync(nativeMiniMapToken));
    }

    private void CancelMatchOperations()
    {
        CancelMapObservation();
        EndAdaptiveMapOpen("match lifecycle changed");
        CancelOrbTracking("match lifecycle changed");
        InvalidateActiveMapOpenOperation("match lifecycle changed");
        _lowStructureRecoveryCursor.Reset();
        try
        {
            _matchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal and match shutdown can race during application exit.
        }
        _alignmentCommitGuard.Invalidate();
        _gameMapToggleState.Reset();
    }

    private CancellationTokenSource BeginMapOpenCancellationScope()
    {
        lock (_mapOpenCancellationGate)
        {
            // Map-open alignment is latest-wins. Revoke the previous owner's
            // token before publishing a new scope; the toggle-version checks
            // below remain the independent commit guard.
            _mapOpenCancellation?.Cancel();
            var scope = CancellationTokenSource.CreateLinkedTokenSource(
                CurrentMatchCancellationToken);
            _mapOpenCancellation = scope;
            return scope;
        }
    }

    private void CompleteMapOpenCancellationScope(
        CancellationTokenSource scope)
    {
        lock (_mapOpenCancellationGate)
        {
            if (ReferenceEquals(_mapOpenCancellation, scope))
                _mapOpenCancellation = null;
        }
        scope.Dispose();
    }

    private void CancelMapOpenAlignment()
    {
        lock (_mapOpenCancellationGate)
            _mapOpenCancellation?.Cancel();
        _lowStructureRecoveryCursor.Reset();
    }

    private async Task DrainMatchOperationsAsync()
    {
        await _observationTask;
        await _nativeMiniMapCaptureTask;
        await DrainOrbTrackingAsync();
        await _scanGate.WaitAsync();
        _scanGate.Release();
    }

    /// <summary>
    /// A quick scan is an explicit request to identify the map again. Release
    /// every map-scoped lock before the new scan starts so a previous wrong
    /// choice cannot constrain the result or remain visible when rescanning
    /// fails or is cancelled.
    /// </summary>
    private void UnlockMapForRescan()
    {
        _hasCompletedQuickScanAlignment = false;
        CancelMapObservation(clearPreview: true);
        _overlay.ClearMap();
        _overlayStatus.Clear();
        RefreshMiniMapForCurrentFloor();
        // 再次快捷扫描是显式请求重新识别：作废尚未消费的后台扫描结果。
        ClearPendingBackgroundScan();
        _lowStructureRecoveryCursor.Reset();
        EndAdaptiveMapOpen("map rescan requested");
        InvalidateActiveMapOpenOperation("map rescan requested");
        var previousMapId = _lastRecognition?.Map.Id
            ?? _pendingAlignmentIdentity?.Map.Id;
        if (previousMapId is null
            && _lastAlignmentSession is null
            && _primaryFloorAlignmentSession is null)
        {
            return;
        }

        _overlayStatus.Clear();
        _overlay.Clear();
        MapOverlayBitmapRenderer.InvalidateImageCache();
        _mapOpenSession.Close("quick scan restarted");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _recognition.ResetMatchState();
        _captureSvc.Reset();

        _currentFloorKey = null;
        _mapLease.Clear();
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        ClearAdaptiveSessionKeys();
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;

        lock (_reliableFloorAlignmentGate)
        {
            _reliableFloorAlignments.Clear();
        }
        ClearManualFloorScaleLocks();
        ClearMapViewportPresenceReferences();

        // Do not allow samples collected for a wrongly selected map to be
        // persisted after a later scan corrects the identity.
        ResetAutomaticMapCacheSamples();

        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"重新扫描已解除地图锁定 · previousMap={previousMapId?.ToString() ?? "<none>"}");
    }

    private void ResetMatchTransientState(bool resetAutomaticCacheSamples)
    {
        CancelMapObservation(clearPreview: true);
        // 对局结束：作废尚未消费的后台扫描结果，下一局重新开始。
        ClearPendingBackgroundScan();
        ClearOptimisticPresentation();
        _lowStructureRecoveryCursor.Reset();
        EndAdaptiveMapOpen("match transient state reset");
        InvalidateActiveMapOpenOperation("match transient state reset");
        _overlayStatus.Clear();
        _overlay.Clear();
        MapOverlayBitmapRenderer.InvalidateImageCache();
        _mapOpenSession.Close("match lifecycle reset");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _gameMapToggleState.Reset();
        _recognition.ResetMatchState();
        _captureSvc.Reset();

        _activeCandidateSelector = null;
        _lastCandidateChoices = [];
        ClearMapLearningContext();
        _manualSelectionActive = false;
        _currentFloorKey = null;
        _mapLease.Clear();
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        ClearAdaptiveSessionKeys();
        _lastDiagnostics = null;
        _lastScanPhaseTimings = null;
        _lastAlignmentPhaseTimings = null;
        _lastScanOperationTrace = null;
        _lastAlignmentOperationTrace = null;
        _lastCandidateOperationTrace = null;
        _lastStableCaptureFailureReason = null;
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;
        lock (_reliableFloorAlignmentGate)
            _reliableFloorAlignments.Clear();
        ClearManualFloorScaleLocks();
        ClearMapViewportPresenceReferences();
        lock (_notifiedVpsg3DegradationKeys)
            _notifiedVpsg3DegradationKeys.Clear();

        if (resetAutomaticCacheSamples)
            ResetAutomaticMapCacheSamples();

        // This reset runs both when a match starts and when it ends. Keep the
        // lifecycle probe observational: a blocking compacting Gen2 collection
        // plus EmptyWorkingSet here made "Start match" page out the whole
        // process and disguised resident startup caches as released memory.
        RealtimePerformanceTracker.AuditLifecycle(
            "ResetMatchTransientState",
            detail: $"resetAutoSamples={resetAutomaticCacheSamples}");
    }

    // ════════════════ ISessionOrchestrator ════════════════

    public Task BeginMatchAsync() => Task.CompletedTask;
    public async Task RunScanAsync() => await Task.CompletedTask;

    public async Task BeginMatchAsync(string mapClass)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureMapCacheSynchronizedAsync();
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            if (_matchSession.Snapshot.IsStarted)
                throw new InvalidOperationException("A match is already in progress.");
            ResetMatchTransientState(resetAutomaticCacheSamples: true);
            _matchPluginsActivated = false;
            StartMatchCancellationScope();
            var match = _matchSession.Begin(mapClass);
            if (_settings?.DiagnosticModeEnabled is true)
                MapDiagnosticModeCapture.BeginMatch();
            await SetMatchPluginsActivatedCoreAsync(true);
            _statusMessage = $"对局已开始 · {mapClass}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"进入对局 · version={match.Version} · class={match.MapClass}");
            StateChanged?.Invoke(this, EventArgs.Empty);

            // Native capture is acquired by its actual consumers. Starting a WGC
            // session here records every game frame even when heading is disabled
            // and recognition is using GDI, until the match is reset.
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }

    [Obsolete("Player slots are no longer used. Call BeginMatchAsync(mapClass).")]
    public Task BeginMatchAsync(PlayerSlot playerSlot, string mapClass) =>
        BeginMatchAsync(mapClass);
    public Task EndMatchAsync() => EndMatchAsync(saveAutomaticMapCache: false);
}
