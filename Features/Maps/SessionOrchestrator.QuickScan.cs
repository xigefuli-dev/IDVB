using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public Task RunQuickScanAsync() => RunQuickScanAsync(candidateSelector: null);

    public async Task RunQuickScanAsync(
        IMapCandidateSelector? candidateSelector)
    {
        var scanStartedAt = Stopwatch.GetTimestamp();
        _lastCandidateChoices = [];
        if (_disposed)
            return;
        _lastScanPhaseTimings = null;
        _lastScanOperationTrace = null;
        if (!_initialized || _settings is null)
        {
            ReportCliGuardFailure("地图运行时尚未初始化。", MapLogCategory.Session);
            return;
        }
        if (!_settings.IsEnabled)
        {
            ReportCliGuardFailure("地图识别功能已禁用。", MapLogCategory.Session);
            return;
        }

        CancelMapObservation(clearPreview: true);

        var scanGeneration = Interlocked.Increment(ref _scanRequestGeneration);
        var scanScope = BeginQuickScanCancellationScope();
        var scanCancellation = scanScope.Token;
        using var scanExecution = ScanExecutionContext.Enter(_settings.ScanPerformanceMode, scanCancellation,
            () => scanGeneration == Volatile.Read(ref _scanRequestGeneration), scanStartedAt);
        try
        {
            // A not-yet-started match intentionally has a cancelled match token. Report
            // that guard before entering cancellation-aware queue/cache waits.
            if (!_matchSession.Snapshot.IsStarted)
            {
                _statusMessage = "请先在对局控件中点击“进入对局”，再执行扫描。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            try
            {
                await EnsureMapCacheSynchronizedAsync().WaitAsync(TimeSpan.FromMilliseconds(
                    Math.Max(1, scanExecution.RemainingMilliseconds - 60)), scanCancellation);
            }
            catch (TimeoutException)
            {
                if (scanExecution.IsSuperseded) return;
                _statusMessage = "地图目录正在更新，本次扫描未能在预算内开始比较。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
            {
                return;
            }
            if (scanExecution.Expired) return;
            if (_settings.BackgroundScanEnabled)
                ClearPendingBackgroundScan();
            var operationMatch = _matchSession.Snapshot;
            if (!_captureSvc.TryGetForegroundClientBounds(
                    out var clientBounds, out var windowHandle, out var failureReason))
            {
                ReportCliCaptureFailure(failureReason);
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
                await RunRecognitionPipelineAsync();
                scanCompleted = backgroundScan
                    ? IsBackgroundScanCompleted
                    : _hasCompletedQuickScanAlignment || _gameMapToggleState.IsOpen
                        && candidateSelector is null && !_settings.SelectMapByTagsEnabled;
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
                var continueObserving = !scanCancellation.IsCancellationRequested && !scanExecution.IsSuperseded
                    && candidateSelector is null;
                CompleteQuickScanCancellationScope(scanScope);
                if (continueObserving) StartMapObservation(delayFirstPass: true);
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
