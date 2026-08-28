// IDVB Remaster — Session Orchestrator 设置器方法

using IDVBuff.Features.QuickStart;
using System.Text;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

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
