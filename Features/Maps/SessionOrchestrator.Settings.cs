// IDVB Remaster — Session Orchestrator 设置器方法

using System.Text;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // ════════════════ Settings ════════════════

    private async Task SaveSettingsAsync()
    {
        if (_settings != null)
            await _settingsRepo.SaveAsync(_settings);
    }

    public async Task SetEnabledAsync(bool v)
    {
        _settings!.IsEnabled = v;
        await SaveSettingsAsync();
        ApplyBindings();
    }
    public async Task SetOverlayStatusVisibleAsync(bool v) { _settings!.ShowOverlayStatus = v; await SaveSettingsAsync(); _overlay.SetStatusVisible(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetReverseAlternateDisplayAsync(bool v) { _settings!.ReverseAlternateDisplay = v; await SaveSettingsAsync(); _overlay.SetReverseAlternateDisplay(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowGateMarkersAsync(bool v) { _settings!.ShowGateMarkers = v; await SaveSettingsAsync(); _overlay.SetShowGateMarkers(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowAuxiliaryAnchorsAsync(bool v) { _settings!.ShowAuxiliaryAnchors = v; await SaveSettingsAsync(); _overlay.SetShowAuxiliaryAnchors(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowTextAnnotationsAsync(bool v) { _settings!.ShowTextAnnotations = v; await SaveSettingsAsync(); _overlay.SetShowTextAnnotations(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowBoxAnnotationsAsync(bool v) { _settings!.ShowBoxAnnotations = v; await SaveSettingsAsync(); _overlay.SetShowBoxAnnotations(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowGateMarkersOnMiniMapAsync(bool v) { _settings!.ShowGateMarkersOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowGateMarkersOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowAuxiliaryAnchorsOnMiniMapAsync(bool v) { _settings!.ShowAuxiliaryAnchorsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowAuxiliaryAnchorsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowTextAnnotationsOnMiniMapAsync(bool v) { _settings!.ShowTextAnnotationsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowTextAnnotationsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowBoxAnnotationsOnMiniMapAsync(bool v) { _settings!.ShowBoxAnnotationsOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowBoxAnnotationsOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetShowFloorOnMiniMapAsync(bool v) { _settings!.ShowFloorOnMiniMap = v; await SaveSettingsAsync(); _overlay.SetShowFloorOnMiniMap(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapScaleAsync(double v) { _settings!.MiniMapScale = v; await SaveSettingsAsync(); _overlay.SetMiniMapScale(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapOpacityAsync(double v) { _settings!.MiniMapOpacity = v; await SaveSettingsAsync(); _overlay.SetMiniMapOpacity(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapOffsetXAsync(double v) { _settings!.MiniMapOffsetX = v; await SaveSettingsAsync(); _overlay.SetMiniMapOffsetX(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetMiniMapOffsetYAsync(double v) { _settings!.MiniMapOffsetY = v; await SaveSettingsAsync(); _overlay.SetMiniMapOffsetY(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusOpacityAsync(double v) { _settings!.StatusOpacity = v; await SaveSettingsAsync(); _overlay.SetStatusOpacity(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusOffsetXAsync(double v) { _settings!.StatusOffsetX = v; await SaveSettingsAsync(); _overlay.SetStatusOffsetX(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetStatusOffsetYAsync(double v) { _settings!.StatusOffsetY = v; await SaveSettingsAsync(); _overlay.SetStatusOffsetY(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetCollectLogsAsync(bool v) { _settings!.CollectLogs = v; _logCollector.IsEnabled = v; await SaveSettingsAsync(); }

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
    public async Task SetCollectAlignmentResearchDataAsync(bool v) { _settings!.CollectAlignmentResearchData = v; await SaveSettingsAsync(); }
    public async Task SetAllowMapExtendBeyondBoundsAsync(bool v) { _settings!.AllowMapExtendBeyondBounds = v; await SaveSettingsAsync(); _overlay.SetAllowExtend(v); await SaveOverlayConfigToPresetAsync(); }
    public async Task SetPersistentMiniMapEnabledAsync(bool v) { _settings!.PersistentMiniMapEnabled = v; await SaveSettingsAsync(); }
    public async Task SetPlayerTrackingEnabledAsync(bool v) { _settings!.PlayerTrackingEnabled = v; await SaveSettingsAsync(); }
    public async Task SetAllowAutomaticMapCacheAsync(bool v) { _settings!.AllowAutomaticMapCache = v; await SaveSettingsAsync(); }
    public async Task SetSkipFloorRecognitionAsync(bool v) { _settings!.SkipFloorRecognition = v; await SaveSettingsAsync(); }
    public async Task SetSkipStabilityConfirmationAsync(bool v) { await SaveSettingsAsync(); }
    public async Task SetMediumConfidenceAsync(double v) { await SaveSettingsAsync(); }

    public async Task SetBindingAsync(MapRuntimeBindingTarget target, MapInputBinding binding)
    {
        switch (target)
        {
            case MapRuntimeBindingTarget.QuickScan: _settings!.QuickScanBinding = binding; break;
            case MapRuntimeBindingTarget.OverlayToggle: _settings!.OverlayToggleBinding = binding; break;
            case MapRuntimeBindingTarget.ManualRecognition: _settings!.ManualRecognitionBinding = binding; break;
            case MapRuntimeBindingTarget.GameMapToggle: _settings!.GameMapToggleBinding = binding; break;
            case MapRuntimeBindingTarget.ControlPanelToggle: _settings!.ControlPanelToggleBinding = binding; break;
            case MapRuntimeBindingTarget.SwitchFloor: _settings!.SwitchFloorBinding = binding; break;
            case MapRuntimeBindingTarget.SaveMapCache: _settings!.SaveMapCacheBinding = binding; break;
        }
        await SaveSettingsAsync();
        ApplyBindings();
    }

    private void ApplyBindings()
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

        _overlay.SetShowGateMarkers(s.ShowGateMarkers);
        _overlay.SetShowAuxiliaryAnchors(s.ShowAuxiliaryAnchors);
        _overlay.SetShowTextAnnotations(s.ShowTextAnnotations);
        _overlay.SetShowBoxAnnotations(s.ShowBoxAnnotations);

        _overlay.SetShowGateMarkersOnMiniMap(s.ShowGateMarkersOnMiniMap);
        _overlay.SetShowAuxiliaryAnchorsOnMiniMap(s.ShowAuxiliaryAnchorsOnMiniMap);
        _overlay.SetShowTextAnnotationsOnMiniMap(s.ShowTextAnnotationsOnMiniMap);
        _overlay.SetShowBoxAnnotationsOnMiniMap(s.ShowBoxAnnotationsOnMiniMap);
        _overlay.SetShowFloorOnMiniMap(s.ShowFloorOnMiniMap);

        _overlay.SetStatusOpacity(s.StatusOpacity);
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

    // ════════════════ TOML Write-back ════════════════

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
        sb.AppendLine($"status_offset_x = {s.StatusOffsetX:F0}");
        sb.AppendLine($"status_offset_y = {s.StatusOffsetY:F0}");
        sb.AppendLine($"persistent_minimap_enabled = {Bool(s.PersistentMiniMapEnabled)}");
        sb.AppendLine($"minimap_opacity = {s.MiniMapOpacity:F2}");
        sb.AppendLine($"minimap_offset_x = {s.MiniMapOffsetX:F0}");
        sb.AppendLine($"minimap_offset_y = {s.MiniMapOffsetY:F0}");
        sb.AppendLine($"minimap_scale = {s.MiniMapScale:F3}");
        sb.AppendLine($"show_gate_markers = {Bool(s.ShowGateMarkers)}");
        sb.AppendLine($"show_auxiliary_anchors = {Bool(s.ShowAuxiliaryAnchors)}");
        sb.AppendLine($"show_text_annotations = {Bool(s.ShowTextAnnotations)}");
        sb.AppendLine($"show_box_annotations = {Bool(s.ShowBoxAnnotations)}");
        sb.AppendLine($"allow_map_extend_beyond_bounds = {Bool(s.AllowMapExtendBeyondBounds)}");
        sb.AppendLine($"show_gate_markers_on_minimap = {Bool(s.ShowGateMarkersOnMiniMap)}");
        sb.AppendLine($"show_auxiliary_anchors_on_minimap = {Bool(s.ShowAuxiliaryAnchorsOnMiniMap)}");
        sb.AppendLine($"show_text_annotations_on_minimap = {Bool(s.ShowTextAnnotationsOnMiniMap)}");
        sb.AppendLine($"show_box_annotations_on_minimap = {Bool(s.ShowBoxAnnotationsOnMiniMap)}");
        sb.AppendLine($"show_floor_on_minimap = {Bool(s.ShowFloorOnMiniMap)}");
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
