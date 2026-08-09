using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IOverlayWindow 适配器 — 委托给 MapOverlayWindow。</summary>
public sealed class OverlayWindowAdapter : IOverlayWindow
{
    private readonly MapOverlayWindow _window = new();

    public bool IsVisible => _window.IsVisible;
    public bool HasMap => _window.HasMap;

    public void UpdateMap(object recognition, object gameBounds, IntPtr gameWindowHandle,
        bool showStatusPreference, object? viewportBounds = null, bool preservePlayer = false)
        => _window.UpdateMap(
            (RuntimeMapRecognition)recognition,
            (MapScreenRect)gameBounds,
            gameWindowHandle,
            showStatusPreference,
            (MapScreenRect?)viewportBounds,
            preservePlayer);

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
    public void SetShowGateMarkers(bool show) => _window.SetShowGateMarkers(show);
    public void SetShowAuxiliaryAnchors(bool show) => _window.SetShowAuxiliaryAnchors(show);
    public void SetShowTextAnnotations(bool show) => _window.SetShowTextAnnotations(show);
    public void SetShowBoxAnnotations(bool show) => _window.SetShowBoxAnnotations(show);
    public void SetShowGateMarkersOnMiniMap(bool show) => _window.SetShowGateMarkersOnMiniMap(show);
    public void SetShowAuxiliaryAnchorsOnMiniMap(bool show) => _window.SetShowAuxiliaryAnchorsOnMiniMap(show);
    public void SetShowTextAnnotationsOnMiniMap(bool show) => _window.SetShowTextAnnotationsOnMiniMap(show);
    public void SetShowBoxAnnotationsOnMiniMap(bool show) => _window.SetShowBoxAnnotationsOnMiniMap(show);
    public void SetShowFloorOnMiniMap(bool show) => _window.SetShowFloorOnMiniMap(show);
    public void SetStatusOpacity(double opacity) => _window.SetStatusOpacity(opacity);
    public void SetStatusOffsetX(double offsetX) => _window.SetStatusOffsetX(offsetX);
    public void SetStatusOffsetY(double offsetY) => _window.SetStatusOffsetY(offsetY);
    public void SetMiniMapOpacity(double opacity) => _window.SetMiniMapOpacity(opacity);
    public void SetMiniMapOffsetX(double offsetX) => _window.SetMiniMapOffsetX(offsetX);
    public void SetMiniMapOffsetY(double offsetY) => _window.SetMiniMapOffsetY(offsetY);
    public void SetMiniMapScale(double scale) => _window.SetMiniMapScale(scale);

    public void Dispose() => _window.Dispose();
}
