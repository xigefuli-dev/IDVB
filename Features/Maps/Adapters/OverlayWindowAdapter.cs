using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IOverlayWindow 适配器 — 委托给 MapOverlayWindow。</summary>
public sealed class OverlayWindowAdapter : IOverlayWindow
{
    private readonly MapOverlayWindow _window;

    public OverlayWindowAdapter(ICaptureProtectionService? captureProtection = null)
    {
        _window = new MapOverlayWindow(captureProtection);
    }

    public bool IsVisible => _window.IsVisible;
    public bool HasMap => _window.HasMap;
    public double? CurrentMiniMapScale => _window.CurrentMiniMapScale;
    public double? CurrentMiniMapWidth => _window.CurrentMiniMapWidth;
    public double? CurrentMiniMapHeight => _window.CurrentMiniMapHeight;
    public bool IsCaptureExclusionEnabled => _window.IsCaptureExclusionEnabled;

    public void UpdateMap(object recognition, object gameBounds, IntPtr gameWindowHandle,
        bool showStatusPreference, object? viewportBounds = null, bool preservePlayer = false)
        => _window.UpdateMap(
            (RuntimeMapRecognition)recognition,
            (MapScreenRect)gameBounds,
            gameWindowHandle,
            showStatusPreference,
            (MapScreenRect?)viewportBounds,
            preservePlayer);

    public void UpdateMapTransform(object transform, bool preservePlayer = true)
        => _window.UpdateMapTransform((MapOverlayTransform)transform, preservePlayer);

    public bool TrySetCaptureExclusion(bool enabled, out string failureReason)
        => _window.TrySetCaptureExclusion(enabled, out failureReason);

    public void UpdateStatus(object status, object gameBounds, IntPtr gameWindowHandle,
        bool showStatusPreference, bool showImmediately = true)
        => _window.UpdateStatus(
            (MapOverlayStatus)status,
            (MapScreenRect)gameBounds,
            gameWindowHandle,
            showStatusPreference,
            showImmediately);

    public void ClearStatus() => _window.ClearStatus();

    public void UpdatePlayer(object? player)
        => _window.UpdatePlayer((MapPlayerState?)player);

    public void Show() => _window.Show();
    public void Hide() => _window.Hide();
    public IDisposable DeferPresent() => _window.DeferPresent();
    public int PresentCount => _window.PresentCount;
    public void SetMainContentVisible(bool visible) => _window.SetMainContentVisible(visible);
    public void Toggle() => _window.Toggle();
    public void Clear() => _window.Clear();
    public void ClearMap() => _window.ClearMap();
    public void ClearSession() => _window.ClearSession();

    public void LockBackground(object recognition, object viewportBounds, object gameBounds,
        IntPtr gameWindowHandle, bool showStatusPreference, bool preservePlayer = false)
        => _window.LockBackground(
            (RuntimeMapRecognition)recognition,
            (MapScreenRect)viewportBounds,
            (MapScreenRect)gameBounds,
            gameWindowHandle,
            showStatusPreference,
            preservePlayer);

    public void SetPersistentMiniMapState(string imagePath, object transform, object gameBounds,
        IntPtr gameWindowHandle, double miniMapScale, object? anchors = null,
        object? annotations = null, string? floorLabel = null)
        => _window.SetPersistentMiniMapState(
            imagePath,
            (MapOverlayTransform)transform,
            (MapScreenRect)gameBounds,
            gameWindowHandle,
            miniMapScale,
            (IReadOnlyList<MapOverlayRenderAnchor>?)anchors,
            (IReadOnlyList<MapOverlayRenderAnnotation>?)annotations,
            floorLabel);

    public void ClearPersistentMiniMap() => _window.ClearPersistentMiniMap();

    // ── 显示设置转发 ──

    public void SetStatusVisible(bool visible) => _window.SetStatusVisible(visible);
    public void SetReverseAlternateDisplay(bool enabled) => _window.SetReverseAlternateDisplay(enabled);
    public void SetAllowExtend(bool allow) => _window.SetAllowExtend(allow);
    public void SetMapOpacity(double opacity) => _window.SetMapOpacity(opacity);
    public void SetShowGateMarkers(bool show) => _window.SetShowGateMarkers(show);
    public void SetShowAuxiliaryAnchors(bool show) => _window.SetShowAuxiliaryAnchors(show);
    public void SetShowTextAnnotations(bool show) => _window.SetShowTextAnnotations(show);
    public void SetShowBoxAnnotations(bool show) => _window.SetShowBoxAnnotations(show);
    public void SetShowLineAnnotations(bool show) => _window.SetShowLineAnnotations(show);
    public void SetShowGateMarkersOnMiniMap(bool show) => _window.SetShowGateMarkersOnMiniMap(show);
    public void SetShowAuxiliaryAnchorsOnMiniMap(bool show) => _window.SetShowAuxiliaryAnchorsOnMiniMap(show);
    public void SetShowTextAnnotationsOnMiniMap(bool show) => _window.SetShowTextAnnotationsOnMiniMap(show);
    public void SetShowBoxAnnotationsOnMiniMap(bool show) => _window.SetShowBoxAnnotationsOnMiniMap(show);
    public void SetShowLineAnnotationsOnMiniMap(bool show) => _window.SetShowLineAnnotationsOnMiniMap(show);
    public void SetShowFloorOnMiniMap(bool show) => _window.SetShowFloorOnMiniMap(show);
    public void SetStatusOpacity(double opacity) => _window.SetStatusOpacity(opacity);
    public void SetStatusScale(double scale) => _window.SetStatusScale(scale);
    public void SetStatusOffsetX(double offsetX) => _window.SetStatusOffsetX(offsetX);
    public void SetStatusOffsetY(double offsetY) => _window.SetStatusOffsetY(offsetY);
    public void SetMiniMapOpacity(double opacity) => _window.SetMiniMapOpacity(opacity);
    public void SetMiniMapOffsetX(double offsetX) => _window.SetMiniMapOffsetX(offsetX);
    public void SetMiniMapOffsetY(double offsetY) => _window.SetMiniMapOffsetY(offsetY);
    public void SetMiniMapScale(double scale) => _window.SetMiniMapScale(scale);
    public void ClearTemporaryMiniMapScales() => _window.ClearTemporaryMiniMapScales();

    public void Dispose() => _window.Dispose();
}
/*
 * 文件职责：OverlayWindowAdapter。
 * 所属模块：Features/Maps，主要负责地图功能与基础设施之间的适配边界。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
