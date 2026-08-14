// IDVB Remaster — 视口校准 TOML（viewport.toml）的 POCO。
// 存储「校准地图区域」的归一化矩形（0..1 相对坐标），随分辨率预设携带。

namespace IDVBuff.Core.Models;

/// <summary>
/// 专属分辨率预设目录下 viewport.toml 的 [viewport] 段。
/// 属性必须可写（get; set;），否则 TOML 提供方通过反射写入时会静默跳过。
/// </summary>
public sealed class ViewportCalibrationConfig
{
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public double MapRegionX { get; set; }
    public double MapRegionY { get; set; }
    public double MapRegionWidth { get; set; }
    public double MapRegionHeight { get; set; }
}
