// IDVB Remaster — Session Orchestrator（新架构唯一入口）
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator : ISessionOrchestrator, IDisposable, IAsyncDisposable
{
    private async Task<IReadOnlyList<string>> GetMapClassesAsync()
    {
        var snapshot = await _mapRepo.GetCatalogSnapshotAsync();
        return snapshot is MapCatalogSnapshot cs ? cs.Classes : Array.Empty<string>();
    }

    // ════════════════ Public Methods ════════════════

    public async Task RefreshMapCacheAsync(Guid? changedMapId = null)
    {
        await _recognition.RefreshCacheAsync(changedMapId);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RunQuickScanAsync() => RunQuickScanAsync(candidateSelector: null);

    public async Task RunQuickScanAsync(
        IMapCandidateSelector? candidateSelector)
    {
        _lastCandidateChoices = [];
        if (_disposed)
            return;
        _lastScanPhaseTimings = null;
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
        if (!_matchSession.Snapshot.IsStarted)
        {
            _statusMessage = "请先在对局控件中点击“进入对局”，再执行扫描。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
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
        if (restoreOverlay)
            _overlay.Hide();
        try
        {
            await RunRecognitionPipelineAsync();
        }
        finally
        {
            _scanProgressOverlay.Complete();
            if (restoreOverlay
                && IsCurrentMatchOperation(operationMatch)
                && !_overlay.IsVisible)
                _overlay.Show();
            _activeCandidateSelector = null;
            Interlocked.Decrement(ref _activeScanOperations);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Runs the existing map-open alignment entry point against the locked
    /// recognition result.  It never invokes the scan/identification pipeline.
    /// </summary>
    public async Task RunAlignmentAsync()
    {
        if (_disposed)
            return;
        _lastAlignmentPhaseTimings = null;
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

        if (!_matchSession.Snapshot.IsStarted)
        {
            _statusMessage = "请先在对局控件中点击“进入对局”，再执行对齐。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_lastRecognition is null && _pendingAlignmentIdentity is null)
        {
            _statusMessage = "尚未锁定地图，请先按快捷扫描键确认地图。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_gameMapToggleState.IsOpen)
        {
            _statusMessage = "游戏地图未打开，请先打开游戏地图后再执行对齐。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_captureSvc.TryGetForegroundClientBounds(
                out _, out _, out var failureReason))
        {
            ReportCliCaptureFailure(failureReason);
            return;
        }

        var transition = new MapGameToggleTransition(
            IsOpen: true,
            Version: _gameMapToggleState.Version);
        Interlocked.Increment(ref _activeScanOperations);
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await RunMapOpenAlignmentAsync(transition);
        }
        finally
        {
            Interlocked.Decrement(ref _activeScanOperations);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Synchronizes the external game's map state without running a scan.</summary>
    public void SynchronizeExternalGameMapState(bool isOpen)
    {
        if (_gameMapToggleState.IsOpen == isOpen)
            return;
        _gameMapToggleState.SetOpenForExternalController(isOpen);
        if (!isOpen)
        {
            CancelOrbTracking("external game map closed");
            _overlay.ClearMap();
            RefreshMiniMapForCurrentFloor();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReportCliCaptureFailure(string failureReason)
    {
        _statusMessage = string.IsNullOrWhiteSpace(failureReason)
            ? "地图截图失败。"
            : failureReason;
        _logCollector.Append(
            MapLogCategory.ViewportCapture,
            MapLogLevel.Warning,
            _statusMessage);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryValidateCliCaptureTarget()
    {
        if (_captureSvc.TryGetForegroundClientBounds(
                out _, out _, out var failureReason))
            return true;

        ReportCliCaptureFailure(failureReason);
        return false;
    }

    private void ReportCliGuardFailure(string message, MapLogCategory category)
    {
        _statusMessage = message;
        _logCollector.Append(category, MapLogLevel.Warning, message);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyDictionary<string, double> BuildAlignmentPhaseTimings(
        MapScanDiagnostics? diagnostics,
        double wallClockMilliseconds)
    {
        if (diagnostics is null)
            return new Dictionary<string, double>
            {
                ["wall_clock"] = wallClockMilliseconds
            };

        return new Dictionary<string, double>
        {
            ["input_to_alignment_start"] = diagnostics.InputToAlignmentStartMilliseconds,
            ["opening_animation_wait"] = diagnostics.OpeningAnimationWaitMilliseconds,
            ["stable_viewport_wait"] = diagnostics.StableViewportWaitMilliseconds,
            ["stable_viewport_capture"] = diagnostics.StableViewportCaptureMilliseconds,
            ["alignment_capture"] = diagnostics.AlignmentCaptureMilliseconds,
            ["alignment_dispatch"] = diagnostics.AlignmentDispatchMilliseconds,
            ["reference_image_load"] = diagnostics.ReferenceImageLoadMilliseconds,
            ["reference_cache"] = diagnostics.ReferenceCacheMilliseconds,
            ["gate_detection"] = diagnostics.GateDetectionMilliseconds,
            ["live_structure_preprocess"] = diagnostics.LiveStructurePreprocessMilliseconds,
            ["structure_preprocess"] = diagnostics.StructurePreprocessMilliseconds,
            ["structure_search"] = diagnostics.StructureSearchMilliseconds,
            ["structure_refine"] = diagnostics.StructureRefineMilliseconds,
            ["session_commit"] = diagnostics.SessionCommitMilliseconds,
            ["overlay"] = diagnostics.OverlayMilliseconds,
            ["alignment_pipeline"] = diagnostics.AlignmentPipelineMilliseconds,
            ["algorithm_total"] = diagnostics.TotalMilliseconds,
            ["wall_clock"] = wallClockMilliseconds
        };
    }

    public void ToggleOverlay()
    {
        if (_disposed || _settings is null || !_settings.IsEnabled)
            return;

        if (_overlay.HasMap || _overlay.IsVisible)
        {
            _overlay.Toggle();
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Info,
                $"Overlay 已切换 · visible={_overlay.IsVisible} · hasMap={_overlay.HasMap}");
            return;
        }

        if (!_captureSvc.TryGetForegroundClientBounds(
                out var clientBoundsObj,
                out var windowHandle,
                out var failureReason)
            || clientBoundsObj is not MapScreenRect gameBounds)
        {
            _statusMessage = $"Overlay 无法显示：{failureReason}";
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        const string title = "Overlay 已响应 F5";
        const string message = "当前尚未加载地图。请打开游戏地图后再按地图键，或等待识别完成。";
        _overlayStatus.Show(
            new MapOverlayStatus(
                MapOverlayStatusLevel.Warning,
                title,
                message,
                $"窗口句柄 0x{windowHandle.ToInt64():X}"),
            gameBounds,
            windowHandle,
            showStatusPreference: true,
            transient: true);
        _overlay.Show();
        _logCollector.Append(
            MapLogCategory.Overlay,
            MapLogLevel.Info,
            $"F5 已响应，但当前没有地图内容 · visible={_overlay.IsVisible}",
            details: new()
            {
                ["hasMap"] = _overlay.HasMap,
                ["windowHandle"] = $"0x{windowHandle.ToInt64():X}",
                ["bounds"] = $"{gameBounds.X:F0},{gameBounds.Y:F0},{gameBounds.Width:F0}x{gameBounds.Height:F0}"
            });
    }

    public void ToggleControlPanel()
    {
        if (_disposed || !_settings!.IsEnabled || _controlPanel is null) return;
        if (_controlPanel.IsVisible)
        {
            _controlPanel.Hide();
            _statusMessage = "外置控件层已隐藏。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_manualSelectionActive) return;
        _ = ToggleControlPanelAsync();
    }

    private async Task ToggleControlPanelAsync()
    {
        if (_controlPanel is null) return;
        try
        {
            if (!_captureSvc.TryGetForegroundClientBounds(
                out var clientBoundsObj, out var hwnd, out _))
                return;
            if (clientBoundsObj is not MapScreenRect gameBounds)
                return;
            await _controlPanel.ShowAsync(gameBounds, hwnd, _matchSession.Snapshot);
        }
        catch { /* 控制面板显示失败不阻塞 */ }
    }

    public bool TryCaptureCalibrationFrame(
        out CapturedGameFrame? frame, out string failureReason)
    {
        if (_captureSvc.TryCaptureClient(out var frameObj, out failureReason))
        {
            frame = frameObj as CapturedGameFrame;
            return frame != null;
        }
        frame = null;
        return false;
    }

    public bool TryRestartElevated(out string failureReason) =>
        GameProcessIntegrityService.TryRestartElevated(out failureReason);

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void CheckIntegrityAndNotify()
    {
        IntegrityStatus = GameProcessIntegrityService.Check();
        if (IntegrityStatus.RequiresElevation && !_elevationEventRaised)
        {
            _elevationEventRaised = true;
            ElevationRequiredDetected?.Invoke(this, EventArgs.Empty);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ════════════════ Dispose ════════════════

    public void Dispose() { _ = DisposeAsync(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _matchLifecycleGate.WaitAsync();
        try
        {
            // Stop producing new match-scoped work before draining anything
            // already queued. Keep the lifecycle gate alive after disposal so
            // a cache hotkey callback queued just before ClearBindings can
            // acquire it, observe the ended match, and return safely.
            _input.ClearBindings();
            Volatile.Write(ref _matchEnding, 1);
            if (_matchSession.Snapshot.IsStarted)
                _matchSession.End();
            _lifetimeCts.Cancel();
            CancelMatchOperations();
            await DrainMatchOperationsAsync();
            await DrainMapCacheWritesAsync();
            ResetMatchTransientState(resetAutomaticCacheSamples: true);
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
        _input.Dispose();
        _overlayStatus.Dispose();
        _scanProgressOverlay.Dispose();
        _overlay.Dispose();
        _gateDetector.Dispose();
        _floorRecognizer.Dispose();
        _playerMarkerSvc.Dispose();
        _recognition.Dispose();
        _playerMarkerDetector.Dispose();
        _controlPanel?.Dispose();
        _surveyCoordinator.StatusChanged -= SurveyCoordinator_StatusChanged;
        await _researchCollector.DisposeAsync();
        await _logCollector.DisposeAsync();
        _initializeGate.Dispose();
        _scanGate.Dispose();
        _matchCancellation?.Dispose();
        _mapCacheWriteGate.Dispose();
        _lifetimeCts.Dispose();
        MapLogCollector.Instance = null!;
        StateChanged = null;
        ElevationRequiredDetected = null;
    }
}
