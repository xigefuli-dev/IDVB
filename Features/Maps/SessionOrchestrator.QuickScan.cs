using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public Task RunQuickScanAsync() => RunQuickScanAsync(candidateSelector: null);

    private async Task RunQuickScanCoreAsync(
        IMapCandidateSelector? candidateSelector)
    {
        var scanStartedAt = ScanRequestDiagnostics.Current?.StartedTimestamp ?? Stopwatch.GetTimestamp();
        _lastCandidateChoices = [];
        if (_disposed)
        {
            LogScanCheckpoint("guard", "rejected", "disposed");
            return;
        }
        _lastScanPhaseTimings = null;
        _lastScanOperationTrace = null;
        if (!_initialized || _settings is null)
        {
            ReportCliGuardFailure("地图运行时尚未初始化。", MapLogCategory.Session);
            LogScanCheckpoint("guard", "rejected", "runtime-not-initialized");
            return;
        }
        if (!_settings.IsEnabled)
        {
            ReportCliGuardFailure("地图识别功能已禁用。", MapLogCategory.Session);
            LogScanCheckpoint("guard", "rejected", "runtime-disabled");
            return;
        }

        CancelMapObservation(clearPreview: true);
        // Rescanning supersedes the old map's alignment before waiting for its
        // gate. Otherwise recovery of a wrong manual choice consumes this scan.
        Interlocked.Increment(ref _continuousAlignmentGeneration);
        InvalidateActiveMapOpenOperation("quick scan requested");

        var scanGeneration = Interlocked.Increment(ref _scanRequestGeneration);
        var scanScope = BeginQuickScanCancellationScope();
        var scanCancellation = scanScope.Token;
        using var scanExecution = ScanExecutionContext.Enter(_settings.ScanPerformanceMode, scanCancellation,
            () => scanGeneration == Volatile.Read(ref _scanRequestGeneration), scanStartedAt);
        _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            "扫描请求已创建", details: new()
            {
                ["generation"] = scanGeneration, ["matchVersion"] = _matchSession.Snapshot.Version,
                ["activeScans"] = _activeScanOperations,
                ["gateAvailable"] = _scanGate.CurrentCount, ["mapOpen"] = _gameMapToggleState.IsOpen
            });
        try
        {
            // A not-yet-started match intentionally has a cancelled match token. Report
            // that guard before entering cancellation-aware queue/cache waits.
            if (!_matchSession.Snapshot.IsStarted)
            {
                _statusMessage = "请先在对局控件中点击“进入对局”，再执行扫描。";
                LogScanCheckpoint("guard", "rejected", "match-not-started");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            try
            {
                LogScanCheckpoint("cache-wait");
                await EnsureMapCacheSynchronizedAsync().WaitAsync(TimeSpan.FromMilliseconds(
                    Math.Max(1, scanExecution.RemainingMilliseconds - 60)), scanCancellation);
                LogScanCheckpoint("cache-ready");
            }
            catch (TimeoutException)
            {
                if (scanExecution.IsSuperseded)
                {
                    LogScanCheckpoint("cache-wait", "superseded", "new-scan-request");
                    return;
                }
                _statusMessage = "地图目录正在更新，本次扫描未能在预算内开始比较。";
                LogScanCheckpoint("cache-wait", "timed-out", "cache-wait-deadline");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
            {
                LogScanCheckpoint("cache-wait", scanExecution.IsSuperseded ? "superseded" : "cancelled",
                    scanExecution.IsSuperseded ? "new-scan-request" : "scan-cancelled");
                return;
            }
            if (scanExecution.Expired)
            {
                LogScanCheckpoint("before-capture", scanExecution.IsSuperseded ? "superseded"
                    : scanCancellation.IsCancellationRequested ? "cancelled" : "timed-out", scanExecution.ComputeStopReason);
                return;
            }
            if (_settings.BackgroundScanEnabled)
                ClearPendingBackgroundScan();
            var operationMatch = _matchSession.Snapshot;
            if (!_captureSvc.TryGetForegroundClientBounds(
                    out var clientBounds, out var windowHandle, out var failureReason))
            {
                ReportCliCaptureFailure(failureReason);
                LogScanCheckpoint("capture-target", "rejected", "foreground-unavailable:" + failureReason);
                return;
            }

            _activeCandidateSelector = candidateSelector;
            _statusMessage = "快速扫描中……";
            StateChanged?.Invoke(this, EventArgs.Empty);

            // 小型进度窗口独立于现有全屏地图 Overlay，只在 GUI 模式显示。
            if (!_headless && clientBounds is MapScreenRect gameBounds)
                _scanProgressOverlay.Show(gameBounds, windowHandle, "正在扫描...");

            Interlocked.Increment(ref _activeScanOperations);
            StateChanged?.Invoke(this, EventArgs.Empty);
            var restoreOverlay = _overlay.IsVisible;
            var backgroundScan = _settings.BackgroundScanEnabled;
            var scanCompleted = false;
            if (restoreOverlay)
                _overlay.Hide();
            try
            {
                _hasCompletedQuickScanAlignment = false;
                LogScanCheckpoint("pipeline");
                if (ScanRequestDiagnostics.Current is { } request) request.PipelineStarted = true;
                await RunRecognitionPipelineAsync();
                scanCompleted = !scanCancellation.IsCancellationRequested
                    && IsCurrentMatchOperation(operationMatch)
                    && (backgroundScan
                    ? _backgroundScanStatus == BackgroundScanStatus.CompletedIdentified
                    : _hasCompletedQuickScanAlignment
                        && _lastRecognition?.Result.OverlayTransform is not null);
                if (ScanRequestDiagnostics.Current is { } terminal)
                {
                    if (scanExecution.IsSuperseded) terminal.Complete("superseded", "new-scan-request");
                    else if (scanCancellation.IsCancellationRequested) terminal.Complete("cancelled", "scan-cancelled");
                    else if (!IsCurrentMatchOperation(operationMatch)) terminal.Complete("superseded", "match-changed");
                    else if (scanCompleted) terminal.Complete("success", backgroundScan ? "background-identified" : "alignment-committed");
                    else if (terminal.Outcome is "pending" or "success")
                        terminal.Complete("failed", "pipeline-returned-without-committed-alignment");
                }
            }
            finally
            {
                if (!scanExecution.IsSuperseded)
                {
                    if (scanCompleted) _scanProgressOverlay.Complete();
                    else _scanProgressOverlay.Fail(GetScanProgressFailureMessage(operationMatch));
                }
                if (!scanExecution.IsSuperseded && restoreOverlay
                    && IsCurrentMatchOperation(operationMatch)
                    && !_overlay.IsVisible)
                    _overlay.Show();
                if (!scanExecution.IsSuperseded) _activeCandidateSelector = null;
                Interlocked.Decrement(ref _activeScanOperations);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            try
            {
                scanExecution.Dispose();
                FinishScanExecution(scanExecution);
            }
            finally
            {
                CompleteQuickScanCancellationScope(scanScope);
            }
        }
    }

    private string GetScanProgressFailureMessage(MapMatchSnapshot operationMatch)
    {
        if (!IsCurrentMatchOperation(operationMatch))
            return "扫描已取消，请重新开始。";
        if (_backgroundScanStatus == BackgroundScanStatus.CompletedFailed
            && !string.IsNullOrWhiteSpace(_pendingBackgroundFailureReason))
        {
            return _pendingBackgroundFailureReason;
        }
        return string.IsNullOrWhiteSpace(_statusMessage)
            || string.Equals(_statusMessage, "快速扫描中……", StringComparison.Ordinal)
                ? "扫描失败，请查看状态或日志后重试。"
                : _statusMessage;
    }

}
