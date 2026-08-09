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
        await _scanGate.WaitAsync();
        _scanGate.Release();
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
