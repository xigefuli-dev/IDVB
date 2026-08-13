namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunMapOpenAlignmentAsync(
        MapGameToggleTransition toggle)
    {
        CancelOrbTracking("absolute alignment started");
        await DrainOrbTrackingAsync();
        var operationMatch = _matchSession.Snapshot;
        var cancellationToken = CurrentMatchCancellationToken;
        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有识别正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var restoreOverlay = _overlay.IsVisible;
        if (restoreOverlay)
            _overlay.Hide();
        try
        {
            await RunMapOpenAlignmentCoreAsync(
                toggle,
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"仅对齐已取消 · matchVersion={operationMatch.Version}");
        }
        finally
        {
            if (restoreOverlay
                && IsCurrentMatchOperation(operationMatch)
                && !_overlay.IsVisible)
            {
                _overlay.Show();
            }
            _scanGate.Release();
        }
    }

    private async Task RunRecognitionPipelineAsync()
    {
        CancelOrbTracking("recognition scan started");
        await DrainOrbTrackingAsync();
        var operationMatch = _matchSession.Snapshot;
        var cancellationToken = CurrentMatchCancellationToken;
        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有扫描正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            UnlockMapForRescan();
            await RunRecognitionPipelineCoreAsync(
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"快捷扫描已取消 · matchVersion={operationMatch.Version}");
        }
        finally
        {
            _scanGate.Release();
        }
    }
}
