using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private Func<bool> CaptureRecognitionContinuationGuard(
        MapMatchSnapshot operationMatch, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _currentMapOpenGeneration);
        var toggleVersion = _gameMapToggleState.Version;
        var manualFloor = _settings!.DisableAutoFloor;
        var floorKey = _currentFloorKey;
        var scan = ScanExecutionContext.Current;
        // A first recognition has no locked identity yet. Its operation epoch,
        // input transition and manual floor policy still own the continuation.
        return () => !_disposed && !cancellationToken.IsCancellationRequested
            && IsCurrentMatchOperation(operationMatch)
            && generation == Volatile.Read(ref _currentMapOpenGeneration)
            && toggleVersion == _gameMapToggleState.Version
            && manualFloor == _settings.DisableAutoFloor
            && (!manualFloor || string.Equals(floorKey, _currentFloorKey, StringComparison.Ordinal))
            && (scan is null || (!scan.CancellationToken.IsCancellationRequested
                && !scan.IsSuperseded && scan.CanCompute));
    }

    private bool CanPublishRecognition(Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isCurrent()) return true;
        ActiveOperationTrace?.SetTerminal("superseded", "recognition-operation-context-changed");
        return false;
    }

    private async Task<AdaptiveAlignmentDecision?> EvaluateRecognitionAdaptiveAsync(
        RuntimeMapRecognition recognition, CapturedGameFrame frame, MapScanDiagnostics? diagnostics,
        Func<bool> isCurrent, CancellationToken cancellationToken, MapFeatureCacheSource? source = null)
    {
        try
        {
            return await EvaluateAdaptiveInitialAsync(recognition, frame, diagnostics, source,
                isCurrent, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !isCurrent())
        {
            ActiveOperationTrace?.SetTerminal("superseded", "recognition-operation-context-changed");
            return null;
        }
    }
}
