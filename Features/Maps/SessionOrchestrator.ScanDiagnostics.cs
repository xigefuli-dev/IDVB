namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void ReportInputDecision(string action, string outcome, string reason)
    {
        if (MapInputOperationContext.Current is { } input)
        {
            input.Outcome = outcome;
            input.Reason = reason;
        }
        _logCollector.Append(MapLogCategory.System,
            outcome == "rejected" ? MapLogLevel.Warning : MapLogLevel.Info,
            $"输入处理决策 · action={action} · outcome={outcome} · reason={reason}", details: new()
            {
                ["action"] = action, ["outcome"] = outcome, ["reason"] = reason,
                ["runtimeEnabled"] = _settings?.IsEnabled,
                ["matchStarted"] = _matchSession.Snapshot.IsStarted,
                ["matchVersion"] = _matchSession.Snapshot.Version,
                ["mapOpen"] = _gameMapToggleState.IsOpen,
                ["currentMapId"] = _lastRecognition?.Map.Id,
                ["pendingIdentityMapId"] = _pendingAlignmentIdentity?.Map.Id,
                ["currentFloor"] = _currentFloorKey
            });
    }

    public async Task RunQuickScanAsync(IMapCandidateSelector? candidateSelector)
    {
        using var request = new ScanRequestDiagnostics();
        var input = MapInputOperationContext.Current;
        if (input is not null) input.ScanId = request.ScanId;
        LogScanCheckpoint("request");
        try
        {
            await RunQuickScanCoreAsync(candidateSelector);
        }
        catch (OperationCanceledException)
        {
            request.Complete("cancelled", "operation-cancelled");
            throw;
        }
        catch (Exception exception)
        {
            request.Complete("failed", "exception:" + exception.GetType().FullName);
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                "扫描请求异常", details: new() { ["exception"] = exception.ToString() });
            throw;
        }
        finally
        {
            if (request.Outcome == "pending")
                request.Complete("failed", "returned-without-terminal-verdict");
            if (input is not null)
            {
                input.Outcome = request.Outcome;
                input.Reason = request.Reason;
            }
            _logCollector.Append(MapLogCategory.ScanLifecycle,
                request.Outcome == "success" ? MapLogLevel.Info : MapLogLevel.Warning,
                $"扫描请求结束 · outcome={request.Outcome} · reason={request.Reason}",
                elapsedMs: request.ElapsedMilliseconds,
                details: ScanCheckpointDetails(request));
        }
    }

    private void LogScanCheckpoint(string stage, string? outcome = null, string? reason = null,
        ScanRequestDiagnostics? capturedRequest = null)
    {
        var request = capturedRequest ?? ScanRequestDiagnostics.Current;
        if (request is null) return;
        request.Stage = stage;
        if (outcome is not null) request.Complete(outcome, reason ?? stage);
        _logCollector.Append(MapLogCategory.ScanLifecycle,
            outcome is null ? MapLogLevel.Info : MapLogLevel.Warning,
            $"扫描检查点 · stage={stage}" + (reason is null ? "" : $" · reason={reason}"),
            details: ScanCheckpointDetails(request));
    }

    private Dictionary<string, object?> ScanCheckpointDetails(ScanRequestDiagnostics request) => new()
    {
        ["scanId"] = request.ScanId, ["stage"] = request.Stage,
        ["inputOperationId"] = request.InputOperationId,
        ["requestElapsedMs"] = request.ElapsedMilliseconds,
        ["outcome"] = request.Outcome, ["reason"] = request.Reason,
        ["pipelineStarted"] = request.PipelineStarted, ["operationId"] = request.TraceId,
        ["disposed"] = _disposed, ["initialized"] = _initialized,
        ["runtimeEnabled"] = _settings?.IsEnabled,
        ["matchStarted"] = _matchSession.Snapshot.IsStarted,
        ["matchVersion"] = _matchSession.Snapshot.Version,
        ["mapClass"] = _matchSession.Snapshot.MapClass,
        ["mapOpen"] = _gameMapToggleState.IsOpen,
        ["gateAvailable"] = _scanGate.CurrentCount, ["activeScans"] = _activeScanOperations,
        ["backgroundScan"] = _settings?.BackgroundScanEnabled,
        ["mode"] = _settings?.ScanPerformanceMode.ToString(),
        ["statusMessage"] = _statusMessage,
        ["currentMapId"] = _lastRecognition?.Map.Id,
        ["currentFloor"] = _currentFloorKey,
        ["pendingIdentityMapId"] = _pendingAlignmentIdentity?.Map.Id,
        ["currentAlignmentCompleted"] = _hasCompletedQuickScanAlignment,
        ["snapshotKind"] = "current-runtime-at-checkpoint"
    };
}
