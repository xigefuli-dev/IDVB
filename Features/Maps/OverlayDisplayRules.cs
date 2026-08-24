// IDVB Remaster — Overlay Display Rules
// 从 TOML 配置读取叠加层默认显示参数，遵循 GateTemplateRules 模式。

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

/// <summary>叠加层显示规则，可由 IConfigProvider 覆盖。</summary>
internal static class OverlayDisplayRules
{
    private static OverlayDisplayConfig _config = new();

    // ── 状态层 ──
    public static double StatusOpacity => _config.StatusOpacity;
    public static double StatusScale => _config.StatusScale;
    public static double StatusOffsetX => _config.StatusOffsetX;
    public static double StatusOffsetY => _config.StatusOffsetY;
    public static bool ShowOverlayStatus => _config.ShowOverlayStatus;

    // ── 小地图 ──
    public static double MinimapOpacity => _config.MinimapOpacity;
    public static double MinimapOffsetX => _config.MinimapOffsetX;
    public static double MinimapOffsetY => _config.MinimapOffsetY;
    public static double MinimapScale => _config.MinimapScale;
    public static bool PersistentMinimapEnabled => _config.PersistentMinimapEnabled;

    // ── 大地图 ──
    public static double MapOpacity => _config.MapOpacity;

    // ── 大地图标记可见性 ──
    public static bool ShowGateMarkers => _config.ShowGateMarkers;
    public static bool ShowAuxiliaryAnchors => _config.ShowAuxiliaryAnchors;
    public static bool ShowTextAnnotations => _config.ShowTextAnnotations;
    public static bool ShowBoxAnnotations => _config.ShowBoxAnnotations;
    public static bool ShowLineAnnotations => _config.ShowLineAnnotations;

    // ── 小地图标记可见性 ──
    public static bool ShowGateMarkersOnMinimap => _config.ShowGateMarkersOnMinimap;
    public static bool ShowAuxiliaryAnchorsOnMinimap => _config.ShowAuxiliaryAnchorsOnMinimap;
    public static bool ShowTextAnnotationsOnMinimap => _config.ShowTextAnnotationsOnMinimap;
    public static bool ShowBoxAnnotationsOnMinimap => _config.ShowBoxAnnotationsOnMinimap;
    public static bool ShowLineAnnotationsOnMinimap => _config.ShowLineAnnotationsOnMinimap;
    public static bool ShowFloorOnMinimap => _config.ShowFloorOnMinimap;

    // ── 其它 ──
    public static bool AllowMapExtendBeyondBounds => _config.AllowMapExtendBeyondBounds;
    public static bool ReverseAlternateDisplay => _config.ReverseAlternateDisplay;

    /// <summary>从 IConfigProvider 的 "overlay" 节读取配置。</summary>
    internal static void ApplyConfig(IConfigProvider provider)
    {
        _config = provider.Get<OverlayDisplayConfig>("overlay") ?? new OverlayDisplayConfig();
    }
}
/*
 * 文件职责：OverlayDisplayRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
