// IDVB Remaster — 分辨率预设解析。
// 纯静态、无副作用，供对局激活时的预设选择与校准写回目标解析共用，
// 便于单元测试。

using CoreResolutionTuningProfile = IDVBuff.Core.Models.ResolutionTuningProfile;

namespace IDVBuff.Features.Maps;

public static class ResolutionPresetResolver
{
    /// <summary>
    /// 按窗口客户区物理尺寸 + DPI 在预设列表中自动匹配预设名。
    /// 顺序：精确宽/高 → ±100px 模糊 → 宽高比近似（容差 &lt;0.05）。
    /// DPI 仅作为次级偏好，不做硬过滤。
    /// </summary>
    public static string? MatchPresetName(
        IReadOnlyList<CoreResolutionTuningProfile> profiles,
        int width,
        int height,
        int dpi)
    {
        if (profiles.Count == 0)
            return null;

        // 1. 精确匹配物理宽/高
        var exact = profiles
            .Where(p => p.ClientWidth == width && p.ClientHeight == height)
            .OrderByDescending(p => p.Dpi == dpi)
            .FirstOrDefault();
        if (exact is not null)
            return exact.Name;

        // 2. 模糊匹配（宽高差各 ≤100px）
        const int tolerance = 100;
        var fuzzy = profiles
            .Where(p =>
                Math.Abs(p.ClientWidth - width) <= tolerance
                && Math.Abs(p.ClientHeight - height) <= tolerance)
            .OrderBy(p =>
                Math.Abs(p.ClientWidth - width)
                + Math.Abs(p.ClientHeight - height))
            .ThenByDescending(p => p.Dpi == dpi)
            .FirstOrDefault();
        if (fuzzy is not null)
            return fuzzy.Name;

        // 3. 宽高比近似匹配
        if (height <= 0)
            return null;
        var targetRatio = (double)width / height;
        const double ratioTolerance = 0.05;
        var ratioMatch = profiles
            .Where(p =>
                p.ClientHeight > 0
                && Math.Abs((double)p.ClientWidth / p.ClientHeight - targetRatio)
                    < ratioTolerance)
            .OrderBy(p =>
                Math.Abs((double)p.ClientWidth / p.ClientHeight - targetRatio))
            .ThenBy(p =>
                Math.Abs(p.ClientWidth - width)
                + Math.Abs(p.ClientHeight - height))
            .ThenByDescending(p => p.Dpi == dpi)
            .FirstOrDefault();
        return ratioMatch?.Name;
    }

    /// <summary>
    /// 解析「生效预设名」：显式指定配置优先（校验存在，失效则回退自动），
    /// 否则（自动）按窗口尺寸自动匹配。
    /// </summary>
    /// <param name="selection">用户选择（null/空 = 自动）。</param>
    public static string? ResolveEffectivePreset(
        string? selection,
        IReadOnlyList<CoreResolutionTuningProfile> profiles,
        int width,
        int height,
        int dpi)
    {
        // The physical client geometry is authoritative. A remembered UI
        // selection must never activate a preset for another resolution.
        return MatchPresetName(profiles, width, height, dpi);
    }
}
/*
 * 文件职责：ResolutionPresetResolver。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
