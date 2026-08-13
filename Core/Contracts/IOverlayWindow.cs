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

    /// <summary>
    /// Excludes the native overlay from desktop capture. Continuous screen
    /// tracking must remain disabled when this cannot be guaranteed.
    /// </summary>
    bool TryEnableCaptureExclusion(out string failureReason)
    {
        failureReason = "Capture exclusion is not supported by this overlay.";
        return false;
    }

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
}
