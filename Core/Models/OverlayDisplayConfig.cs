// IDVB Remaster — Overlay Display Config POCO
// TOML 映射：provider.Get<OverlayDisplayConfig>("overlay") 读取 [overlay] 节

namespace IDVBuff.Core.Models;

/// <summary>
/// 叠加层显示参数配置，可由 TOML 预设覆盖。
/// 属性名遵循 PascalCase，TomlConfigProvider 自动转换为 snake_case 匹配 TOML 键名。
/// </summary>
public sealed class OverlayDisplayConfig
{
    // ── 状态层 ──
    public double StatusOpacity { get; set; } = 1.0;
    public double StatusOffsetX { get; set; } = 0;
    public double StatusOffsetY { get; set; } = 0;
    public bool ShowOverlayStatus { get; set; } = true;

    // ── 小地图 ──
    public double MinimapOpacity { get; set; } = 0.55;
    public double MinimapOffsetX { get; set; } = 0;
    public double MinimapOffsetY { get; set; } = 50;
    public double MinimapScale { get; set; } = 0.25;
    public bool PersistentMinimapEnabled { get; set; } = false;

    // ── 大地图 ──
    public double MapOpacity { get; set; } = 0.46;

    // ── 大地图标记可见性 ──
    public bool ShowGateMarkers { get; set; } = true;
    public bool ShowAuxiliaryAnchors { get; set; } = true;
    public bool ShowTextAnnotations { get; set; } = true;
    public bool ShowBoxAnnotations { get; set; } = true;
    public bool ShowLineAnnotations { get; set; } = true;

    // ── 小地图标记可见性 ──
    public bool ShowGateMarkersOnMinimap { get; set; } = true;
    public bool ShowAuxiliaryAnchorsOnMinimap { get; set; } = true;
    public bool ShowTextAnnotationsOnMinimap { get; set; } = true;
    public bool ShowBoxAnnotationsOnMinimap { get; set; } = true;
    public bool ShowLineAnnotationsOnMinimap { get; set; } = true;
    public bool ShowFloorOnMinimap { get; set; } = false;

    // ── 其它显示选项 ──
    public bool AllowMapExtendBeyondBounds { get; set; } = false;
    public bool ReverseAlternateDisplay { get; set; } = false;
}
