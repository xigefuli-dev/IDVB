namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly SemaphoreSlim _matchLifecycleGate = new(1, 1);
    private CancellationTokenSource? _matchCancellation;
    private int _matchEnding;

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

    private void StartMatchCancellationScope()
    {
        _matchCancellation?.Cancel();
        _matchCancellation?.Dispose();
        _matchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
    }

    private void CancelMatchOperations()
    {
        CancelOrbTracking("match lifecycle changed");
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

    private async Task DrainMatchOperationsAsync()
    {
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
        _mapOpenSession.Close("quick scan restarted");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _recognition.ResetMatchState();

        _currentFloorKey = null;
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;

        lock (_reliableFloorAlignmentGate)
        {
            _reliableFloorAlignments.Clear();
            _reliableFloorAlignmentMatchVersion =
                _matchSession.Snapshot.Version;
        }

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
        _overlayStatus.Clear();
        _overlay.Clear();
        _mapOpenSession.Close("match lifecycle reset");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _gameMapToggleState.Reset();
        _recognition.ResetMatchState();

        _activeCandidateSelector = null;
        _lastCandidateChoices = [];
        _manualSelectionActive = false;
        _currentFloorKey = null;
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        _lastDiagnostics = null;
        _lastScanPhaseTimings = null;
        _lastAlignmentPhaseTimings = null;
        _lastStableCaptureFailureReason = null;
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;

        if (resetAutomaticCacheSamples)
            ResetAutomaticMapCacheSamples();
    }
}
