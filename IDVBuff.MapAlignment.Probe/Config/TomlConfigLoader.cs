using System.Text.Json;
using IDVBuff.Features.Maps;

namespace IDVBuff.MapAlignment.Probe.Config;

/// <summary>
/// 配置加载器。从 settings.json 加载运行设置并合并到选项上下文。
/// 目前仅支持 JSON 格式（TOML 为占位，待后续扩展）。
/// </summary>
public static class TomlConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 从 settings.json 加载 MapRuntimeSettings，并应用到 ProbeContext。
    /// </summary>
    public static async Task<MapRuntimeSettings?> LoadSettingsAsync(string? settingsPath)
    {
        var path = settingsPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IDVBuff",
                "MapRuntime",
                "settings.json");

        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(json, JsonOptions)
                ?? new MapRuntimeSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 settings.json 加载设置，只提取视口和结构配准调优参数，
    /// 合并到给定的 ProbeContext 中。
    /// </summary>
    public static async Task ApplyToContextAsync(
        Pipeline.ProbeContext context,
        string? settingsPath)
    {
        var settings = await LoadSettingsAsync(settingsPath);
        if (settings is null)
            return;

        if (settings.IsMapViewportCalibrated
            && settings.MapViewportRegion is not null
            && context.ViewportRegion is null)
        {
            context.ViewportRegion = settings.MapViewportRegion;
            context.ViewportMargin = settings.StructureRegistrationTuning.MapViewportEdgeMargin;
        }

        if (settings.SelectedMapId.HasValue && context.SideScanMapId is null)
        {
            // 侧门扫描可用选中地图作为默认目标
        }
    }
}
