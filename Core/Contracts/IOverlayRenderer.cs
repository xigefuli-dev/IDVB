// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 叠加层渲染抽象。负责将地图纹理、锚点标注、状态文字、玩家标记等
/// 渲染到一块透明位图上，供原生分层窗口呈现。
/// </summary>
public interface IOverlayRenderer
{
    /// <summary>
    /// 完整渲染叠加场景（地图 + 状态 + 玩家 + 小地图）。
    /// </summary>
    /// <param name="scene">渲染场景描述。</param>
    /// <returns>渲染后的位图（平台相关类型，Phase 0.4 替换为抽象帧缓冲）。</returns>
    // TODO: Phase 0.4 — 替换 Bitmap 为抽象接口
    object /* Bitmap */ Render(object /* MapOverlayRenderScene */ scene);

    /// <summary>
    /// 在已锁定的背景上叠加动态元素（玩家标记、状态文字、小地图），
    /// 避免每帧重绘静态地图。
    /// </summary>
    /// <param name="lockedBackground">已渲染的静态背景位图。</param>
    /// <param name="scene">包含动态元素的渲染场景。</param>
    /// <returns>合成后的位图。</returns>
    // TODO: Phase 0.4 — 替换 Bitmap 为抽象接口
    object /* Bitmap */ ComposeDynamic(
        object /* Bitmap */ lockedBackground,
        object /* MapOverlayRenderScene */ scene);

    /// <summary>
    /// 清除已缓存的图片，用于地图目录刷新后重建。
    /// </summary>
    void InvalidateImageCache();
}
