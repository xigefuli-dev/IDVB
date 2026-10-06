// IDVB Remaster — Session Orchestrator（新架构唯一入口）
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Features.Notifications;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator : ISessionOrchestrator, IDisposable, IAsyncDisposable
{
    private long _scanRequestGeneration;
    private readonly object _quickScanCancellationGate = new();
    private CancellationTokenSource? _quickScanCancellation;
    private async Task<IReadOnlyList<string>> GetMapClassesAsync() =>
        await _mapRepo.GetMapClassesAsync();

    // ════════════════ Public Methods ════════════════

    public async Task RefreshMapCacheAsync(Guid? changedMapId = null)
    {
        Task refresh;
        lock (_mapOpenCancellationOwner.SyncRoot)
        {
            ClearAutomaticIdentityJob();
            // Put the cache writer in the same lane, so a new open cannot
            // start a worker in the gap between draining and refreshing.
            refresh = RefreshAutomaticIdentityCacheAfterWorkerAsync(
                _automaticIdentityWorker, changedMapId);
            _automaticIdentityWorker = refresh;
            ObserveAutomaticIdentityWorkerFault(refresh);
        }
        await refresh;
        if (_learningEngineInitialized)
            await _learningEngine.InvalidateReferenceCacheAsync(
                _lifetimeCts.Token);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshAutomaticIdentityCacheAfterWorkerAsync(
        Task precedingWorker, Guid? changedMapId)
    {
        await ObserveAutomaticIdentityWorkerAsync(precedingWorker);
        _lifetimeCts.Token.ThrowIfCancellationRequested();
        await _recognition.RefreshCacheAsync(changedMapId);
    }

    public async Task EnsureMapCacheSynchronizedAsync()
    {
        if (_recognition.CatalogRevision != _mapRepository.GetCatalogRevision())
        {
            await RefreshMapCacheAsync();
        }
    }

    /// <summary>
    /// Runs the same map-open entry as the GUI. An unidentified match first
    /// resolves identity from the current frame, then publishes its alignment.
    /// </summary>
    public async Task RunAlignmentAsync()
    {
        if (_disposed)
            return;
        _lastAlignmentPhaseTimings = null;
        _lastAlignmentOperationTrace = null;
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
        var operationMatch = _matchSession.Snapshot;
        Interlocked.Increment(ref _activeScanOperations);
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await RunMapOpenAlignmentAsync(transition, continuous: false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeScanOperations);
            if (IsCurrentMatchOperation(operationMatch)
                && _gameMapToggleState.IsCurrent(transition) && CanObserveMap)
                StartMapObservation(delayFirstPass: true);
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
            CancelMapOpenAlignment();
            CancelMapObservation();
            EndAdaptiveMapOpen("external game map closed");
            CancelOrbTracking("external game map closed");
            _overlay.ClearMap();
            RefreshMiniMapForCurrentFloor();
        }
        else if (CanObserveMap && !HasActiveQuickScan)
            StartMapObservation();
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
            ["scan_candidate_count"] = diagnostics.ScanCandidateCount,
            ["scan_verification_candidate_count"] =
                diagnostics.ScanVerificationCandidateCount,
            ["candidate_0_template_validation_ms"] =
                diagnostics.ScanCandidate0TemplateValidationMilliseconds,
            ["candidate_0_vpsg_ms"] = diagnostics.ScanCandidate0VpsgMilliseconds,
            ["candidate_0_structure_ms"] =
                diagnostics.ScanCandidate0StructureMilliseconds,
            ["cheap_reject_count"] = diagnostics.ScanCheapRejectCount,
            ["cheap_reject_ms"] = diagnostics.ScanCheapRejectMilliseconds,
            ["scan_formal_structure_attempt_count"] =
                diagnostics.ScanFormalStructureAttemptCount,
            ["shadow_pair_count"] = diagnostics.ScanShadowPairCount,
            ["shadow_true_formal_false"] =
                diagnostics.ScanShadowTrueFormalFalseCount,
            ["shadow_false_formal_true"] =
                diagnostics.ScanShadowFalseFormalTrueCount,
            ["shadow_true_formal_true"] =
                diagnostics.ScanShadowTrueFormalTrueCount,
            ["shadow_false_formal_false"] =
                diagnostics.ScanShadowFalseFormalFalseCount,
            ["shadow_collection"] = diagnostics.ScanShadowCollectionEnabled ? 1d : 0d,
            ["effective_budget_ms"] =
                diagnostics.ScanEffectiveBudgetMilliseconds,
            ["scan_total_verification_ms"] =
                diagnostics.ScanTotalVerificationMilliseconds,
            ["scan_vpsg_attempt_count"] = diagnostics.ScanVpsgAttemptCount,
            ["scan_full_recovery_count"] = diagnostics.ScanFullRecoveryCount,
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

    private bool _controlPanelToggleInProgress;

    public void ToggleControlPanel() => _ = ObserveControlPanelToggleAsync();

    private async Task ObserveControlPanelToggleAsync()
    {
        using var input = new MapInputOperationContext();
        const string action = "control-panel-toggle";
        LogInputHandlerOutcome(action, "handler-started");
        if (_controlPanelToggleInProgress)
        {
            input.Outcome = "rejected";
            input.Reason = "operation-in-progress";
            LogInputHandlerOutcome(action, "handler-rejected:operation-in-progress");
            return;
        }
        _controlPanelToggleInProgress = true;
        try
        {
            var outcome = await ToggleControlPanelAsync();
            input.Outcome = outcome.StartsWith("handler-rejected:", StringComparison.Ordinal) ? "rejected" : "applied";
            input.Reason = outcome;
            LogInputHandlerOutcome(action, outcome);
        }
        catch (Exception exception)
        {
            _statusMessage = $"外置控件层显示失败：{exception.Message}";
            input.Outcome = "failed";
            input.Reason = exception.GetType().FullName ?? exception.GetType().Name;
            LogInputHandlerOutcome(action, "handler-failed", exception);
        }
        finally
        {
            _controlPanelToggleInProgress = false;
        }
    }

    private async Task<string> ToggleControlPanelAsync()
    {
        if (_disposed) return "handler-rejected:disposed";
        if (_settings is not { IsEnabled: true }) return "handler-rejected:disabled";
        if (_controlPanel is null) return "handler-rejected:panel-unavailable";
        if (_controlPanel.IsVisible)
        {
            _controlPanel.Hide();
            _statusMessage = "外置控件层已隐藏。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return "handler-completed:hidden";
        }
        if (_manualSelectionActive) return "handler-rejected:map-selection-active";
        if (!_captureSvc.TryGetForegroundClientBounds(
            out var clientBoundsObj, out var hwnd, out var reason))
            return $"handler-rejected:foreground-unavailable:{reason}";
        if (clientBoundsObj is not MapScreenRect gameBounds)
            return "handler-rejected:invalid-client-bounds";
        await _controlPanel.ShowAsync(gameBounds, hwnd, _matchSession.Snapshot);
        if (_disposed || _settings is not { IsEnabled: true } || _manualSelectionActive)
        {
            _controlPanel.Hide();
            return "handler-rejected:runtime-changed-during-show";
        }
        return _controlPanel.IsVisible
            ? "handler-completed:shown"
            : "handler-rejected:panel-not-visible";
    }

    public async Task ToggleMatchStateAsync()
    {
        if (_disposed || !_settings!.IsEnabled || _manualSelectionActive) return;
        if (_captureSvc.TryGetForegroundClientBounds(out var clientBoundsObj, out _, out _) &&
            clientBoundsObj is MapScreenRect gameBounds)
        {
            OverlayNotificationCenter.UpdateGameBounds(gameBounds);
        }
        if (_matchSession.Snapshot.IsStarted)
        {
            if (_controlPanel?.IsVisible == true)
                _controlPanel.Hide();
            await EndMatchAsync(saveAutomaticMapCache: false);
            if (!_headless)
            {
                OverlayNotificationCenter.Warning("已结束对局");
            }
        }
        else
        {
            if (_controlPanel?.IsVisible == true)
                _controlPanel.Hide();
            var classes = await GetMapClassesAsync();
            var mapClass = MapRuntimeSettingsRules.ResolveMapClass(classes, _settings.LastSelectedMapClass);
            if (string.IsNullOrWhiteSpace(mapClass))
            {
                _statusMessage = "地图库中还没有可用的地图模式。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!string.Equals(_settings.LastSelectedMapClass, mapClass, StringComparison.Ordinal))
            {
                await SetLastSelectedMapClassAsync(mapClass);
            }
            await BeginMatchAsync(mapClass);
            if (!_headless)
            {
                OverlayNotificationCenter.Notice(!string.IsNullOrWhiteSpace(mapClass) ? $"已进入对局 · {mapClass}" : "已进入对局");
            }
        }
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
            await DrainHumanMapSelectionRecordingAsync();
            await _recognition.ReleaseMatchResourcesAsync();
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
        await _learningEngine.DisposeAsync();
        await DrainAdaptiveScaleAsync();
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
/*
 * 文件职责：SessionOrchestrator.Operations。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
