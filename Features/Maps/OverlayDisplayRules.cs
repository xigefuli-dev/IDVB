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
