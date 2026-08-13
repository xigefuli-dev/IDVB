// IDVB Remaster — 视口校准写回专属分辨率预设目录的 viewport.toml。

using System.Text;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 将「校准地图区域」的归一化矩形写为预设目录下的 viewport.toml。
/// 与 SessionOrchestrator.Settings 中的 overlay.toml 写回保持同一范式。
/// </summary>
public static class ViewportCalibrationTomlWriter
{
    public const string FileName = "viewport.toml";

    /// <summary>生成 viewport.toml 的文本内容（[viewport] 段）。</summary>
    public static string BuildViewportToml(NormalizedRectangle region)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IDVB Viewport Calibration");
        sb.AppendLine();
        sb.AppendLine("[viewport]");
        sb.AppendLine($"map_region_x = {region.X:F6}");
        sb.AppendLine($"map_region_y = {region.Y:F6}");
        sb.AppendLine($"map_region_width = {region.Width:F6}");
        sb.AppendLine($"map_region_height = {region.Height:F6}");
        return sb.ToString();
    }

    /// <summary>写入预设目录；目录不存在时自动创建。</summary>
    public static async Task WriteAsync(
        string presetDirectory,
        NormalizedRectangle region)
    {
        if (!Directory.Exists(presetDirectory))
            Directory.CreateDirectory(presetDirectory);

        var path = Path.Combine(presetDirectory, FileName);
        await File.WriteAllTextAsync(
            path,
            BuildViewportToml(region),
            Encoding.UTF8);
    }
}
