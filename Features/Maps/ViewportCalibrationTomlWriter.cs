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
    public static string BuildViewportToml(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IDVB Viewport Calibration");
        sb.AppendLine();
        sb.AppendLine("[viewport]");
        sb.AppendLine($"client_width = {clientWidth}");
        sb.AppendLine($"client_height = {clientHeight}");
        sb.AppendLine($"map_region_x = {region.X:F6}");
        sb.AppendLine($"map_region_y = {region.Y:F6}");
        sb.AppendLine($"map_region_width = {region.Width:F6}");
        sb.AppendLine($"map_region_height = {region.Height:F6}");
        return sb.ToString();
    }

    /// <summary>写入预设目录；目录不存在时自动创建。</summary>
    public static async Task WriteAsync(
        string presetDirectory,
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight)
    {
        if (!Directory.Exists(presetDirectory))
            Directory.CreateDirectory(presetDirectory);

        var path = Path.Combine(presetDirectory, FileName);
        await File.WriteAllTextAsync(
            path,
            BuildViewportToml(region, clientWidth, clientHeight),
            Encoding.UTF8);
    }
}
/*
 * 文件职责：ViewportCalibrationTomlWriter。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
