// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 叠加窗口管理抽象。控制非激活、可穿透点击的叠加层的显示/隐藏、
/// 位置更新和内容刷新。
/// </summary>
public interface IOverlayWindow : IDisposable
{
    /// <summary>
    /// 叠加层当前是否可见。
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    /// 是否已加载地图内容。
    /// </summary>
    bool HasMap { get; }

    /// <summary>
    /// 更新地图叠加内容（纹理、变换、锚点、标注）。
    /// </summary>
    void UpdateMap(
        object /* RuntimeMapRecognition */ recognition,
        object /* MapScreenRect */ gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        object? /* MapScreenRect? */ viewportBounds = null,
        bool preservePlayer = false);

    /// <summary>Updates only the transform of the currently loaded map.</summary>
    void UpdateMapTransform(
        object /* MapOverlayTransform */ transform,
        bool preservePlayer = true)
    {
    }

    /// <summary>当前原生 Overlay 是否实际成功启用捕获排除。</summary>
    bool IsCaptureExclusionEnabled => false;

    /// <summary>
    /// 设置 Overlay 的捕获排除状态。该方法只改变捕获可见性，不改变窗口显示状态。
    /// </summary>
    bool TrySetCaptureExclusion(bool enabled, out string failureReason)
    {
        failureReason = "Capture exclusion is not supported by this overlay.";
        return false;
    }

    /// <summary>兼容旧调用方的启用别名。</summary>
    bool TryEnableCaptureExclusion(out string failureReason) =>
        TrySetCaptureExclusion(true, out failureReason);

    /// <summary>
    /// 更新叠加状态文字。
    /// </summary>
    void UpdateStatus(
        object /* MapOverlayStatus */ status,
        object /* MapScreenRect */ gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool showImmediately = true);

    /// <summary>Clears only the transient status layer.</summary>
    void ClearStatus();

    /// <summary>
    /// 更新玩家标记位置。
    /// </summary>
    void UpdatePlayer(object? /* MapPlayerState? */ player);

    /// <summary>
    /// 显示叠加层。
    /// </summary>
    void Show();

    /// <summary>
    /// 隐藏叠加层。
    /// </summary>
    void Hide();

    /// <summary>
    /// Defers native presentation until the outermost lease is disposed.
    /// Implementations that do not render may use the no-op default.
    /// </summary>
    IDisposable DeferPresent() => NoopOverlayPresentLease.Instance;

    /// <summary>Number of native presents performed by this overlay instance.</summary>
    int PresentCount => 0;

    /// <summary>临时控制大地图、状态和玩家标记的绘制，不影响持久小地图。</summary>
    void SetMainContentVisible(bool visible)
    {
    }

    /// <summary>
    /// 切换叠加层可见性。
    /// </summary>
    void Toggle();

    /// <summary>
    /// 清除当前对局的所有内容（地图、玩家、小地图、状态）。
    /// </summary>
    void Clear();

    /// <summary>
    /// 清除当前地图内容（保留小地图和状态）。
    /// </summary>
    void ClearMap();

    /// <summary>
    /// 清除当前地图对齐会话下的状态。
    /// </summary>
    void ClearSession();

    /// <summary>
    /// 锁定背景渲染（用于确认后的静态背景缓存）。
    /// </summary>
    void LockBackground(
        object /* RuntimeMapRecognition */ recognition,
        object /* MapScreenRect */ viewportBounds,
        object /* MapScreenRect */ gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool preservePlayer = false);

    /// <summary>
    /// 更新持久小地图内容（图层、变换、锚点、标注、楼层标签）。
    /// 当游戏地图关闭或对局空闲时，小地图作为唯一可见内容保留。
    /// </summary>
    void SetPersistentMiniMapState(
        string imagePath,
        object /* MapOverlayTransform */ transform,
        object /* MapScreenRect */ gameBounds,
        IntPtr gameWindowHandle,
        double miniMapScale,
        object? /* IReadOnlyList<MapOverlayRenderAnchor>? */ anchors = null,
        object? /* IReadOnlyList<MapOverlayRenderAnnotation>? */ annotations = null,
        string? floorLabel = null);

    /// <summary>
    /// 清除持久小地图内容。
    /// </summary>
    void ClearPersistentMiniMap();

    // ════════════════ 显示设置 ════════════════

    void SetStatusVisible(bool visible);
    void SetReverseAlternateDisplay(bool enabled);
    void SetAllowExtend(bool allow);
    void SetMapOpacity(double opacity);
    void SetShowGateMarkers(bool show);
    void SetShowAuxiliaryAnchors(bool show);
    void SetShowTextAnnotations(bool show);
    void SetShowBoxAnnotations(bool show);
    void SetShowLineAnnotations(bool show);
    void SetShowGateMarkersOnMiniMap(bool show);
    void SetShowAuxiliaryAnchorsOnMiniMap(bool show);
    void SetShowTextAnnotationsOnMiniMap(bool show);
    void SetShowBoxAnnotationsOnMiniMap(bool show);
    void SetShowLineAnnotationsOnMiniMap(bool show);
    void SetShowFloorOnMiniMap(bool show);
    void SetStatusOpacity(double opacity);
    void SetStatusOffsetX(double offsetX);
    void SetStatusOffsetY(double offsetY);
    void SetMiniMapOpacity(double opacity);
    void SetMiniMapOffsetX(double offsetX);
    void SetMiniMapOffsetY(double offsetY);
    void SetMiniMapScale(double scale);

    /// <summary>清除当前对局内按楼层保存的临时小地图缩放。</summary>
    void ClearTemporaryMiniMapScales()
    {
    }

    /// <summary>
    /// 当前已加载小地图的临时显示尺度。没有小地图内容时返回 null。
    /// 该值是渲染状态，不代表已持久化的用户设置。
    /// </summary>
    double? CurrentMiniMapScale => null;

}

internal sealed class NoopOverlayPresentLease : IDisposable
{
    public static readonly NoopOverlayPresentLease Instance = new();
    public void Dispose() { }
}
