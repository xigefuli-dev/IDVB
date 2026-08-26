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

    public async Task SetEnabledAsync(bool v)
    {
        if (v && !TryValidateEnablePrerequisites(out var failureMessage))
        {
            _settings!.IsEnabled = false;
            await SaveSettingsAsync();
            ApplyBindings();
            throw new InvalidOperationException(failureMessage);
        }
        _settings!.IsEnabled = v;
        await SaveSettingsAsync();
        ApplyBindings();
    }

    public bool TryValidateEnablePrerequisites(out string failureMessage)
    {
        var missing = new List<string>();
        if (App.IsSafeMode)
            missing.Add("关闭安全模式并重新启动 IDVB");
        if (_settings is null || !HasRequiredInputBindings(_settings))
            missing.Add("完成全部按键绑定");
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
        && settings.QuickScanBinding.IsConfigured
        && settings.SwitchFloorBinding.IsConfigured
        && settings.SaveMapCacheBinding.IsConfigured;

    /// <summary>
    /// Applies the first-run recommended profile to the active runtime and
    /// persists it. Values not specified by the recommendation remain at the
    /// normal runtime defaults.
    /// </summary>
    public async Task ApplyQuickStartRecommendedSettingsAsync()
    {
        if (_settings is null)
            throw new InvalidOperationException("SessionOrchestrator has not been initialized.");

        var recommended = QuickStartRecommendedSettings.CreateRecommendation1();
        recommended.Normalize();
        await _researchCollector.SetEnabledAsync(recommended.CollectAlignmentResearchData);
        _settings = recommended;
        _logCollector.IsEnabled = recommended.CollectLogs;
        ApplyBindings();
        ApplyDisplaySettingsToOverlay();
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetOverlayStatusVisibleAsync(bool v) { _settings!.ShowOverlayStatus = v; await SaveSettingsAsync(); _overlay.SetStatusVisible(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetReverseAlternateDisplayAsync(bool v) { _settings!.ReverseAlternateDisplay = v; await SaveSettingsAsync(); _overlay.SetReverseAlternateDisplay(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMapOpacityAsync(double v) { _settings!.MapOpacity = v; await SaveSettingsAsync(); _overlay.SetMapOpacity(v); await SaveOverlayConfigToPresetAsync(); }
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
    public async Task SetCollectLogsAsync(bool v)
    {
        _settings!.CollectLogs = v;
        if (v)
            _logCollector.IsEnabled = true;
        else
            await _logCollector.ClearDataAsync();
        await SaveSettingsAsync();
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
    public async Task SetAllowMapExtendBeyondBoundsAsync(bool v) { _settings!.AllowMapExtendBeyondBounds = v; await SaveSettingsAsync(); _overlay.SetAllowExtend(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetPersistentMiniMapEnabledAsync(bool v) { _settings!.PersistentMiniMapEnabled = v; await SaveSettingsAsync(); }
    public async Task SetPlayerTrackingEnabledAsync(bool v) { _settings!.PlayerTrackingEnabled = v; await SaveSettingsAsync(); }
    public async Task SetAllowAutomaticMapCacheAsync(bool v) { _settings!.AllowAutomaticMapCache = v; await SaveSettingsAsync(); }
    public async Task SetSkipFloorRecognitionAsync(bool v) { _settings!.SkipFloorRecognition = v; await SaveSettingsAsync(); }
    public async Task SetSkipStabilityConfirmationAsync(bool v) { await SaveSettingsAsync(); }
    public async Task SetMediumConfidenceAsync(double v) { await SaveSettingsAsync(); }

    public async Task SetBindingAsync(MapRuntimeBindingTarget target, MapInputBinding binding)
    {
        if (_settings is null)
            throw new InvalidOperationException("SessionOrchestrator has not been initialized.");

        var newBinding = binding.Clone();
        var previousBinding = GetBinding(target).Clone();
        SetBinding(target, newBinding);
        try
        {
            ApplyBindings(throwOnFailure: true);
            await SaveSettingsAsync();
        }
        catch
        {
            SetBinding(target, previousBinding);
            try
            {
                ApplyBindings(throwOnFailure: true);
            }
            catch
            {
                ApplyBindings();
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
            default: throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private void ApplyBindings(bool throwOnFailure = false)
    {
        if (_settings is not { IsEnabled: true })
        {
            _input.ClearBindings();
            return;
        }

        try
        {
            _input.ApplyBindings(
                _settings.QuickScanBinding,
                _settings.OverlayToggleBinding,
                _settings.ManualRecognitionBinding,
                _settings.GameMapToggleBinding,
                _settings.ControlPanelToggleBinding,
                _settings.SwitchFloorBinding,
                _settings.SaveMapCacheBinding);
        }
        catch (Exception ex)
        {
            if (throwOnFailure)
                throw;
            _settings.IsEnabled = false;
            _statusMessage = $"热键注册失败：{ex.Message}";
        }
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

        _overlay.SetShowGateMarkers(s.ShowGateMarkers);
        _overlay.SetShowAuxiliaryAnchors(s.ShowAuxiliaryAnchors);
        _overlay.SetShowTextAnnotations(s.ShowTextAnnotations);
        _overlay.SetShowBoxAnnotations(s.ShowBoxAnnotations);
        _overlay.SetShowLineAnnotations(s.ShowLineAnnotations);

        _overlay.SetShowGateMarkersOnMiniMap(s.ShowGateMarkersOnMiniMap);
        _overlay.SetShowAuxiliaryAnchorsOnMiniMap(s.ShowAuxiliaryAnchorsOnMiniMap);
        _overlay.SetShowTextAnnotationsOnMiniMap(s.ShowTextAnnotationsOnMiniMap);
        _overlay.SetShowBoxAnnotationsOnMiniMap(s.ShowBoxAnnotationsOnMiniMap);
        _overlay.SetShowLineAnnotationsOnMiniMap(s.ShowLineAnnotationsOnMiniMap);
        _overlay.SetShowFloorOnMiniMap(s.ShowFloorOnMiniMap);

        _overlay.SetStatusOpacity(s.StatusOpacity);
        _overlay.SetStatusScale(s.StatusScale);
        _overlay.SetStatusOffsetX(s.StatusOffsetX);
        _overlay.SetStatusOffsetY(s.StatusOffsetY);

        _overlay.SetMiniMapOpacity(s.MiniMapOpacity);
        _overlay.SetMiniMapOffsetX(s.MiniMapOffsetX);
        _overlay.SetMiniMapOffsetY(s.MiniMapOffsetY);
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
    { _settings!.RecognitionTuning = t; await SaveSettingsAsync(); }
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
    { _settings!.OverlayAlignmentMode = m; await SaveSettingsAsync(); }
    public async Task SetFirstScanStrategyAsync(FirstScanStrategy s)
    { _settings!.FirstScanStrategy = s; await SaveSettingsAsync(); }

    /// <summary>
    /// 后台扫描开关：开启后快捷扫描仅识别不对齐；关闭时防御性作废未消费的后台结果。
    /// </summary>
    public async Task SetBackgroundScanEnabledAsync(bool enabled)
    {
        _settings!.BackgroundScanEnabled = enabled;
        await SaveSettingsAsync();
        if (!enabled)
            ClearPendingBackgroundScan();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 会话级后台扫描开关（非持久化）：仅本次运行生效，不写回 settings。
    /// RealCLI 强制候选选择（--candidate）要求后台模式关闭，此方法让自动化
    /// 临时切换而不污染用户配置；关闭时同样防御性作废未消费的后台结果。
    /// </summary>
    public void SetBackgroundScanEnabledForSession(bool enabled)
    {
        if (_settings is null)
            return;
        _settings.BackgroundScanEnabled = enabled;
        if (!enabled)
            ClearPendingBackgroundScan();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 记录「使用配置文件」的用户选择（null/空 = 自动）并持久化。
    /// 仅保存选择，不即时重载 TOML——实际预设在对局控件激活时解析生效。
    /// </summary>
    public async Task SetSelectedResolutionPresetAsync(string? presetNameOrNull)
    {
        var normalized = string.IsNullOrWhiteSpace(presetNameOrNull)
            ? null
            : presetNameOrNull.Trim();
        if (_settings!.SelectedResolutionPreset == normalized)
            return;
        _settings.SelectedResolutionPreset = normalized;
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ════════════════ TOML Write-back ════════════════

    /// <summary>
    /// 将当前校准地图区域写回目标预设目录的 viewport.toml。
    /// 目标预设由用户选择解析：指定配置→该配置；自动→按窗口实际分辨率匹配。
    /// </summary>
    private async Task WriteViewportCalibrationToPresetAsync(
        int clientWidth,
        int clientHeight,
        uint observedDpi)
    {
        var region = _settings!.GetExactDisplayCalibration(
            clientWidth,
            clientHeight)?.MapViewportRegion;
        if (region?.IsValid is not true)
            return;

        try
        {
            var target = ResolutionPresetResolver.MatchPresetName(
                GetAvailablePresets(),
                clientWidth,
                clientHeight,
                observedDpi > 0 ? (int)observedDpi : 120);
            if (string.IsNullOrWhiteSpace(target))
                return;

            var presetDir = _config.ResolvePresetDirectory(target);
            await ViewportCalibrationTomlWriter.WriteAsync(
                presetDir,
                region,
                clientWidth,
                clientHeight);

            // 写回目标正是当前活跃预设时，需重载合并表，否则下一次
            // ResolveViewportRegion 仍会读到旧的 viewport.toml（同名切换会因
            // SetActivePreset 的早期返回而不触发重载）。
            var activeGeometry = _config.ActiveResolutionPreset.Split(' ')[0];
            var targetGeometry = target.Split(' ')[0];
            if (string.Equals(
                activeGeometry,
                targetGeometry,
                StringComparison.OrdinalIgnoreCase))
            {
                _config.Reload();
            }
        }
        catch (Exception ex)
        {
            // viewport.toml 写回失败不应影响主流程
            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                $"viewport.toml 写回失败：{ex.Message}");
        }
    }

    /// <summary>将当前显示设置写回活跃预设的 overlay.toml。</summary>
    private async Task SaveOverlayConfigToPresetAsync()
    {
        try
        {
            var presetDir = _config.ResolvePresetDirectory(_config.ActiveResolutionPreset);
            if (!Directory.Exists(presetDir))
                Directory.CreateDirectory(presetDir);

            var path = Path.Combine(presetDir, "overlay.toml");
            var toml = BuildOverlayToml(_settings!);
            await File.WriteAllTextAsync(path, toml, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // TOML 写回失败不应影响主流程
            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                $"overlay.toml 写回失败：{ex.Message}");
        }
    }

    private static string BuildOverlayToml(MapRuntimeSettings s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IDVB Overlay Parameters");
        sb.AppendLine();
        sb.AppendLine("[overlay]");
        sb.AppendLine($"show_overlay_status = {Bool(s.ShowOverlayStatus)}");
        sb.AppendLine($"reverse_alternate_display = {Bool(s.ReverseAlternateDisplay)}");
        sb.AppendLine($"status_opacity = {s.StatusOpacity:F1}");
        sb.AppendLine($"status_scale = {s.StatusScale:F3}");
        sb.AppendLine($"status_offset_x = {s.StatusOffsetX:F3}");
        sb.AppendLine($"status_offset_y = {s.StatusOffsetY:F3}");
        sb.AppendLine($"persistent_minimap_enabled = {Bool(s.PersistentMiniMapEnabled)}");
        sb.AppendLine($"minimap_opacity = {s.MiniMapOpacity:F2}");
        sb.AppendLine($"minimap_offset_x = {s.MiniMapOffsetX:F3}");
        sb.AppendLine($"minimap_offset_y = {s.MiniMapOffsetY:F3}");
        sb.AppendLine($"minimap_scale = {s.MiniMapScale:F3}");
        sb.AppendLine($"map_opacity = {s.MapOpacity:F2}");
        sb.AppendLine($"show_gate_markers = {Bool(s.ShowGateMarkers)}");
        sb.AppendLine($"show_auxiliary_anchors = {Bool(s.ShowAuxiliaryAnchors)}");
        sb.AppendLine($"show_text_annotations = {Bool(s.ShowTextAnnotations)}");
        sb.AppendLine($"show_box_annotations = {Bool(s.ShowBoxAnnotations)}");
        sb.AppendLine($"show_line_annotations = {Bool(s.ShowLineAnnotations)}");
        sb.AppendLine($"allow_map_extend_beyond_bounds = {Bool(s.AllowMapExtendBeyondBounds)}");
        sb.AppendLine($"show_gate_markers_on_minimap = {Bool(s.ShowGateMarkersOnMiniMap)}");
        sb.AppendLine($"show_auxiliary_anchors_on_minimap = {Bool(s.ShowAuxiliaryAnchorsOnMiniMap)}");
        sb.AppendLine($"show_text_annotations_on_minimap = {Bool(s.ShowTextAnnotationsOnMiniMap)}");
        sb.AppendLine($"show_box_annotations_on_minimap = {Bool(s.ShowBoxAnnotationsOnMiniMap)}");
        sb.AppendLine($"show_line_annotations_on_minimap = {Bool(s.ShowLineAnnotationsOnMiniMap)}");
        sb.AppendLine($"show_floor_on_minimap = {Bool(s.ShowFloorOnMiniMap)}");
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
/*
 * 文件职责：SessionOrchestrator.Settings。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
