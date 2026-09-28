// IDVB Remaster — Session Orchestrator 设置器方法

using IDVBuff.Features.QuickStart;
using System.Text;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // ════════════════ Settings ════════════════

    /// <summary>
    /// Gets the map Class remembered by the match control panel.
    /// Consumers must treat this as read-only; changing the preference goes
    /// through <see cref="SetLastSelectedMapClassAsync"/>.
    /// </summary>
    public string? LastSelectedMapClass => _settings?.LastSelectedMapClass;

    private async Task SaveSettingsAsync()
    {
        if (_settings != null)
            await _settingsRepo.SaveAsync(_settings);
    }

    /// <summary>
    /// Persists the last map Class selected in the match control panel.
    /// This preference does not affect the current match identity.
    /// </summary>
    public async Task SetLastSelectedMapClassAsync(string mapClass)
    {
        if (_settings is null)
            throw new InvalidOperationException(
                "SessionOrchestrator has not been initialized.");

        var normalized = string.IsNullOrWhiteSpace(mapClass)
            ? null
            : mapClass.Trim();
        if (normalized is null)
            return;
        if (string.Equals(
            _settings.LastSelectedMapClass,
            normalized,
            StringComparison.Ordinal))
        {
            return;
        }

        _settings.LastSelectedMapClass = normalized;
        await SaveSettingsAsync();
    }

    public bool TryValidateEnablePrerequisites(out string failureMessage)
    {
        var missing = new List<string>();
        if (App.IsSafeMode)
            missing.Add("关闭安全模式并重新启动 IDVB");
        if (_settings is null || !HasRequiredInputBindings(_settings))
            missing.Add("完成游戏地图开关、外置控件层和快捷扫描的按键绑定");
        if (_recognition.TotalMapCount < 1)
            missing.Add("至少添加一张地图");

        if (missing.Count == 0)
        {
            failureMessage = string.Empty;
            return true;
        }

        failureMessage = "开启前必须先：" + string.Join("；", missing) + "。";
        return false;
    }

    private static bool HasRequiredInputBindings(MapRuntimeSettings settings) =>
        settings.GameMapToggleBinding.IsConfigured
        && settings.ControlPanelToggleBinding.IsConfigured
        && settings.QuickScanBinding.IsConfigured;

    public async Task SetOverlayStatusVisibleAsync(bool v) { _settings!.ShowOverlayStatus = true; await SaveSettingsAsync(); _overlay.SetStatusVisible(true); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetReverseAlternateDisplayAsync(bool v) { _settings!.ReverseAlternateDisplay = false; await SaveSettingsAsync(); _overlay.SetReverseAlternateDisplay(false); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMapOpacityAsync(double v) { _settings!.MapOpacity = v; await SaveSettingsAsync(); _overlay.SetMapOpacity(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowRoutesAsync(bool v)
    {
        _settings!.ShowRoutes = v;
        _settings.Normalize();
        await SaveSettingsAsync();
        ApplyRouteVisibilityToOverlay(v);
        await SaveOverlayConfigToPresetAsync();
    }
    public async Task SetShowGateMarkersAsync(bool v) { _settings!.ShowGateMarkers = v; await SaveSettingsAsync(); _overlay.SetShowGateMarkers(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowAuxiliaryAnchorsAsync(bool v) { _settings!.ShowAuxiliaryAnchors = v; await SaveSettingsAsync(); _overlay.SetShowAuxiliaryAnchors(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowTextAnnotationsAsync(bool v) { _settings!.ShowTextAnnotations = v; await SaveSettingsAsync(); _overlay.SetShowTextAnnotations(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowBoxAnnotationsAsync(bool v) { _settings!.ShowBoxAnnotations = v; await SaveSettingsAsync(); _overlay.SetShowBoxAnnotations(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowLineAnnotationsAsync(bool v) { _settings!.ShowLineAnnotations = v; await SaveSettingsAsync(); _overlay.SetShowLineAnnotations(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowGateMarkersOnMiniMapAsync(bool v) { _settings!.ShowGateMarkersOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowGateMarkersOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowAuxiliaryAnchorsOnMiniMapAsync(bool v) { _settings!.ShowAuxiliaryAnchorsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowAuxiliaryAnchorsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowTextAnnotationsOnMiniMapAsync(bool v) { _settings!.ShowTextAnnotationsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowTextAnnotationsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowBoxAnnotationsOnMiniMapAsync(bool v) { _settings!.ShowBoxAnnotationsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowBoxAnnotationsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowLineAnnotationsOnMiniMapAsync(bool v) { _settings!.ShowLineAnnotationsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowLineAnnotationsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowFloorOnMiniMapAsync(bool v) { _settings!.ShowFloorOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowFloorOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapScaleAsync(double v) { _settings!.MiniMapScale = v; await SaveSettingsAsync(); _overlay.SetMiniMapScale(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapOpacityAsync(double v) { _settings!.MiniMapOpacity = v; await SaveSettingsAsync(); _overlay.SetMiniMapOpacity(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapOffsetXAsync(double v) { _settings!.MiniMapOffsetX = v; await SaveSettingsAsync(); _overlay.SetMiniMapOffsetX(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapOffsetYAsync(double v) { _settings!.MiniMapOffsetY = v; await SaveSettingsAsync(); _overlay.SetMiniMapOffsetY(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusOpacityAsync(double v) { _settings!.StatusOpacity = v; await SaveSettingsAsync(); _overlay.SetStatusOpacity(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusScaleAsync(double v) { _settings!.StatusScale = v; await SaveSettingsAsync(); _overlay.SetStatusScale(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusOffsetXAsync(double v) { _settings!.StatusOffsetX = v; await SaveSettingsAsync(); _overlay.SetStatusOffsetX(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusOffsetYAsync(double v) { _settings!.StatusOffsetY = v; await SaveSettingsAsync(); _overlay.SetStatusOffsetY(v); await SaveOverlayConfigToPresetAsync(); }
    /// <summary>
    /// Changes the remembered map Class for the current headless session only.
    /// Replay and diagnostic callers must not persist their per-case fixture.
    /// </summary>
    public void SetLastSelectedMapClassForSession(string mapClass)
    {
        if (_settings is null)
            throw new InvalidOperationException(
                "SessionOrchestrator has not been initialized.");

        var normalized = string.IsNullOrWhiteSpace(mapClass)
            ? null
            : mapClass.Trim();
        if (normalized is not null)
            _settings.LastSelectedMapClass = normalized;
    }

    public async Task SetDiagnosticModeAsync(bool enabled)
    {
        if (!enabled)
            MapDiagnosticModeCapture.Clear();
        _settings!.DiagnosticModeEnabled = enabled;
        await SaveSettingsAsync();
        if (enabled && _matchSession.Snapshot.IsStarted)
            MapDiagnosticModeCapture.BeginMatch();
    }

    /// <summary>
    /// Enables structured diagnostics for the in-process CLI without changing
    /// the user's persisted settings.  CLI output must contain the same
    /// MapLogCollector entries as the GUI runtime, including failures.
    /// </summary>
    public void EnableCliDiagnostics()
    {
        if (_disposed)
            return;
        _logCollector.IsEnabled = true;
    }

    /// <summary>
    /// Installs the test controller's XButton1 binding for this process only.
    /// The setting is deliberately not persisted, so an overlay_game test
    /// cannot silently change the player's GUI configuration.
    /// </summary>
    public void UseCliGameMapXButton1Binding()
    {
        if (_settings is null)
            return;
        _settings.GameMapToggleBinding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Mouse,
            MouseButton = MapMouseButton.XButton1
        };
        ApplyBindings();
    }
    public async Task SetCollectAlignmentResearchDataAsync(bool enabled)
    {
        if (_settings is null)
            throw new InvalidOperationException("SessionOrchestrator has not been initialized.");

        var previous = _settings.CollectAlignmentResearchData;
        if (previous == enabled && _researchCollector.IsEnabled == enabled)
            return;

        try
        {
            if (enabled)
                await _researchCollector.SetEnabledAsync(true);
            else
                await _researchCollector.ClearDataAsync();
            _settings.CollectAlignmentResearchData = enabled;
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            _settings.CollectAlignmentResearchData = previous;
            try
            {
                await _researchCollector.SetEnabledAsync(previous);
            }
            catch (Exception rollbackException)
            {
                _logCollector.Append(
                    MapLogCategory.System,
                    MapLogLevel.Error,
                    "研究数据采集器状态回滚失败。",
                    details: new()
                    {
                        ["exceptionType"] = rollbackException.GetType().FullName,
                        ["exception"] = rollbackException.ToString()
                    });
            }

            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                "研究数据采集设置未生效，已保留旧状态。",
                details: new()
                {
                    ["requestedEnabled"] = enabled,
                    ["previousEnabled"] = previous,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
            throw;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task SetAllowMapExtendBeyondBoundsAsync(bool v) { _settings!.AllowMapExtendBeyondBounds = true; await SaveSettingsAsync(); _overlay.SetAllowExtend(true); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetPersistentMiniMapEnabledAsync(bool v) { _settings!.PersistentMiniMapEnabled = true; await SaveSettingsAsync(); }
    public async Task SetPlayerTrackingEnabledAsync(bool v) { _settings!.PlayerTrackingEnabled = false; await SaveSettingsAsync(); }
    public async Task SetAllowAutomaticMapCacheAsync(bool v)
    { _settings!.AllowAutomaticMapCache = false; await SaveSettingsAsync(); }
    public async Task SetSkipFloorRecognitionAsync(bool v) { _settings!.SkipFloorRecognition = v; await SaveSettingsAsync(); }
    public async Task SetSkipStabilityConfirmationAsync(bool v) { await SaveSettingsAsync(); }
    public async Task SetMediumConfidenceAsync(double v) { await SaveSettingsAsync(); }

    public async Task SetBindingAsync(MapRuntimeBindingTarget target, MapInputBinding binding)
    {
        await _inputSettingsGate.WaitAsync();
        try { await SetBindingCoreAsync(target, binding); }
        finally { _inputSettingsGate.Release(); }
    }

    private async Task SetBindingCoreAsync(MapRuntimeBindingTarget target, MapInputBinding binding)
    {
        if (_settings is null)
            throw new InvalidOperationException("SessionOrchestrator has not been initialized.");

        var newBinding = binding.Clone();
        var previousBinding = GetBinding(target).Clone();
        SetBinding(target, newBinding);
        try
        {
            _settings.ValidateInputBindings();
        }
        catch
        {
            // No listener was touched; rejecting a conflicting selection must
            // not stop and reinstall otherwise healthy input monitoring.
            SetBinding(target, previousBinding);
            throw;
        }
        try
        {
            ApplyBindings();
            await SaveSettingsAsync();
        }
        catch (Exception failure)
        {
            SetBinding(target, previousBinding);
            try
            {
                ApplyBindings();
            }
            catch (Exception rollbackFailure)
            {
                _settings.IsEnabled = false;
                CancelMapObservation(clearPreview: true);
                _statusMessage = $"恢复原按键失败：{rollbackFailure.Message}";
                var failures = new List<Exception> { failure, rollbackFailure };
                try { _input.ClearBindings(); }
                catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
                try { await SaveSettingsAsync(); }
                catch (Exception saveFailure) { failures.Add(saveFailure); }
                StateChanged?.Invoke(this, EventArgs.Empty);
                throw new AggregateException("按键设置失败，运行层已关闭。", failures);
            }
            throw;
        }
    }

    private MapInputBinding GetBinding(MapRuntimeBindingTarget target) => target switch
    {
        MapRuntimeBindingTarget.QuickScan => _settings!.QuickScanBinding,
        MapRuntimeBindingTarget.OverlayToggle => _settings!.OverlayToggleBinding,
        MapRuntimeBindingTarget.ManualRecognition => _settings!.ManualRecognitionBinding,
        MapRuntimeBindingTarget.GameMapToggle => _settings!.GameMapToggleBinding,
        MapRuntimeBindingTarget.ControlPanelToggle => _settings!.ControlPanelToggleBinding,
        MapRuntimeBindingTarget.SwitchFloor => _settings!.SwitchFloorBinding,
        MapRuntimeBindingTarget.TraditionalWindowSwitchFloor =>
            _settings!.TraditionalWindowSwitchFloorBinding,
        MapRuntimeBindingTarget.SaveMapCache => _settings!.SaveMapCacheBinding,
        MapRuntimeBindingTarget.RestMapDisplay => _settings!.RestMapDisplayBinding,
        MapRuntimeBindingTarget.MatchStateToggle => _settings!.MatchStateToggleBinding,
        MapRuntimeBindingTarget.HideAlignmentResult =>
            _settings!.HideAlignmentResultBinding,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
    };

    private void SetBinding(MapRuntimeBindingTarget target, MapInputBinding binding)
    {
        switch (target)
        {
            case MapRuntimeBindingTarget.QuickScan: _settings!.QuickScanBinding = binding; break;
            case MapRuntimeBindingTarget.OverlayToggle: _settings!.OverlayToggleBinding = binding; break;
            case MapRuntimeBindingTarget.ManualRecognition: _settings!.ManualRecognitionBinding = binding; break;
            case MapRuntimeBindingTarget.GameMapToggle: _settings!.GameMapToggleBinding = binding; break;
            case MapRuntimeBindingTarget.ControlPanelToggle: _settings!.ControlPanelToggleBinding = binding; break;
            case MapRuntimeBindingTarget.SwitchFloor: _settings!.SwitchFloorBinding = binding; break;
            case MapRuntimeBindingTarget.TraditionalWindowSwitchFloor:
                _settings!.TraditionalWindowSwitchFloorBinding = binding;
                break;
            case MapRuntimeBindingTarget.SaveMapCache: _settings!.SaveMapCacheBinding = binding; break;
            case MapRuntimeBindingTarget.RestMapDisplay: _settings!.RestMapDisplayBinding = binding; break;
            case MapRuntimeBindingTarget.MatchStateToggle: _settings!.MatchStateToggleBinding = binding; break;
            case MapRuntimeBindingTarget.HideAlignmentResult:
                _settings!.HideAlignmentResultBinding = binding;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private void ApplyBindings()
    {
        if (_settings is not { IsEnabled: true })
        {
            CancelMapObservation(clearPreview: true);
            _input.ClearBindings();
            return;
        }

        _settings.ValidateInputBindings();
        _input.ApplyBindings(
                _settings.QuickScanBinding,
                _settings.OverlayToggleBinding,
                _settings.ManualRecognitionBinding,
                _settings.GameMapToggleBinding,
                _settings.ControlPanelToggleBinding,
                _settings.SwitchFloorBinding,
                _settings.SaveMapCacheBinding,
                _settings.RestMapDisplayBinding,
                _settings.MatchStateToggleBinding,
                _settings.HideAlignmentResultBinding);
    }

    /// <summary>将当前显示设置批量推送到叠加层窗口。</summary>
    private void ApplyDisplaySettingsToOverlay()
    {
        if (_settings is null) return;
        var s = _settings;

        _overlay.SetStatusVisible(s.ShowOverlayStatus);
        _overlay.SetReverseAlternateDisplay(s.ReverseAlternateDisplay);
        _overlay.SetAllowExtend(s.AllowMapExtendBeyondBounds);
        _overlay.SetMapOpacity(s.MapOpacity);

        ApplyRouteVisibilityToOverlay(s.ShowRoutes);
        _overlay.SetShowFloorOnMiniMap(s.ShowFloorOnMiniMap);

        _overlay.SetStatusOpacity(s.StatusOpacity);
        _overlay.SetStatusScale(s.StatusScale);
        _overlay.SetStatusOffsetX(s.StatusOffsetX);
        _overlay.SetStatusOffsetY(s.StatusOffsetY);

        _overlay.SetMiniMapOpacity(s.MiniMapOpacity);
        _overlay.SetMiniMapOffsetX(s.MiniMapOffsetX);
        _overlay.SetMiniMapOffsetY(s.MiniMapOffsetY);
    }

    private void ApplyRouteVisibilityToOverlay(bool showRoutes)
    {
        _overlay.SetShowGateMarkers(showRoutes);
        _overlay.SetShowAuxiliaryAnchors(false);
        _overlay.SetShowTextAnnotations(showRoutes);
        _overlay.SetShowBoxAnnotations(showRoutes);
        _overlay.SetShowLineAnnotations(showRoutes);
        _overlay.SetShowGateMarkersOnMiniMap(false);
        _overlay.SetShowAuxiliaryAnchorsOnMiniMap(false);
        _overlay.SetShowTextAnnotationsOnMiniMap(showRoutes);
        _overlay.SetShowBoxAnnotationsOnMiniMap(showRoutes);
        _overlay.SetShowLineAnnotationsOnMiniMap(showRoutes);
    }

    public async Task SetMapViewportAsync(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight,
        uint observedDpi = 0)
    {
        _settings!.UpsertMapViewportCalibration(
            region,
            clientWidth,
            clientHeight,
            observedDpi);
        await SaveSettingsAsync();
        await WriteViewportCalibrationToPresetAsync(
            clientWidth,
            clientHeight,
            observedDpi);
    }

    public async Task SetFloorDisplayRegionAsync(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight,
        uint observedDpi = 0)
    {
        _settings!.UpsertFloorDisplayCalibration(
            region,
            clientWidth,
            clientHeight,
            observedDpi);
        await SaveSettingsAsync();
    }

    // Tuning
    public async Task SetRecognitionTuningAsync(MapRecognitionTuning t)
    { t.ForceBestRecognitionResult = false; t.ForceCandidateSelection = false; t.PlayerDecidesScale = false; _settings!.RecognitionTuning = t; await SaveSettingsAsync(); }
    public async Task SetStructureRegistrationTuningAsync(MapStructureRegistrationTuning t)
    { _settings!.StructureRegistrationTuning = t; await SaveSettingsAsync(); }
    public async Task SetSessionTuningAsync(MapSessionTuning t)
    { _settings!.SessionTuning = t; await SaveSettingsAsync(); }
    public async Task SetFloorRecognitionTuningAsync(MapFloorRecognitionTuning t)
    { _settings!.FloorRecognitionTuning = t; await SaveSettingsAsync(); }
    public async Task SetPlayerTrackingTuningAsync(MapPlayerTrackingTuning t)
    { _settings!.PlayerTrackingTuning = t; await SaveSettingsAsync(); }

    public async Task RestoreRecognitionTuningDefaultsAsync()
    { _settings!.RecognitionTuning = new MapRecognitionTuning(); await SaveSettingsAsync(); }
    public async Task RestoreStructureRegistrationTuningDefaultsAsync()
    { _settings!.StructureRegistrationTuning = new MapStructureRegistrationTuning(); await SaveSettingsAsync(); }
    public async Task RestoreSessionTuningDefaultsAsync()
    { _settings!.SessionTuning = new MapSessionTuning(); await SaveSettingsAsync(); }
    public async Task RestoreFloorRecognitionTuningDefaultsAsync()
    { _settings!.FloorRecognitionTuning = new MapFloorRecognitionTuning(); await SaveSettingsAsync(); }
    public async Task RestorePlayerTrackingTuningDefaultsAsync()
    { _settings!.PlayerTrackingTuning = new MapPlayerTrackingTuning(); await SaveSettingsAsync(); }

    public async Task SetOverlayAlignmentModeAsync(MapOverlayAlignmentMode m)
    { _settings!.OverlayAlignmentMode = MapOverlayAlignmentMode.Uniform; await SaveSettingsAsync(); }
    public async Task SetFirstScanStrategyAsync(FirstScanStrategy s)
    { _settings!.FirstScanStrategy = FirstScanStrategy.SideEntrance; await SaveSettingsAsync(); }

    /// <summary>
    /// 后台扫描已从产品路径移除；保留此兼容入口只会确保它保持关闭。
    /// </summary>
    public async Task SetBackgroundScanEnabledAsync(bool enabled)
    {
        _settings!.BackgroundScanEnabled = false;
        _settings.SilentScanEnabled = false;
        await SaveSettingsAsync();
        ClearPendingBackgroundScan();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>通过标签选择地图开关：开启后快捷扫描直接给出当前地图类的全部结果。</summary>
    public async Task SetSelectMapByTagsEnabledAsync(bool enabled)
    {
        if (_settings is null)
            return;
        _settings.SelectMapByTagsEnabled = enabled;
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>后台扫描已移除；兼容调用不能在会话内重新启用它。</summary>
    public void SetBackgroundScanEnabledForSession(bool enabled)
    {
        if (_settings is null)
            return;
        _settings.BackgroundScanEnabled = false;
        ClearPendingBackgroundScan();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>配置文件选择已移除；运行时始终使用自动解析。</summary>
    public async Task SetSelectedResolutionPresetAsync(string? presetNameOrNull)
    {
        string? normalized = null;
        if (_settings!.SelectedResolutionPreset == normalized)
            return;
        _settings.SelectedResolutionPreset = normalized;
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
