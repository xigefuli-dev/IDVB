namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    public void SetMiniMapScale(double scale)
    {
        if (_persistentMiniMap is not { } miniMap) return;
        scale = Math.Clamp(scale, 0d, 1d);
        if (scale == 0d)
        {
            _persistentMiniMap = miniMap with { Width = 0f, Height = 0f };
            _miniMapScale = 0d;
            if (_miniMapImageKey is not null)
                _miniMapFloorScales.Remember(_miniMapImageKey, 0d);
            InvalidateLockedBackground();
            if (IsVisible)
                Present();
            return;
        }
        if (!MapOverlayBitmapRenderer.TryGetScaledImageSize(
                miniMap.ImagePath, scale, out var width, out var height))
            return;
        _persistentMiniMap = miniMap with { Width = width, Height = height };
        _miniMapScale = scale;
        if (_miniMapImageKey is not null)
            _miniMapFloorScales.Remember(_miniMapImageKey, scale);
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    internal void SetPersistentMiniMapState(
        string imagePath,
        MapOverlayTransform transform,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        double miniMapScale,
        IReadOnlyList<MapOverlayRenderAnchor>? anchors = null,
        IReadOnlyList<MapOverlayRenderAnnotation>? annotations = null,
        string? floorLabel = null)
    {
        _gameBounds = gameBounds;
        _gameWindowHandle = gameWindowHandle;
        var imageKey = Path.GetFullPath(imagePath);
        // Resolve the target floor's final scale before replacing the visible
        // mini-map. The next presentation therefore cannot show a base or
        // previous-floor scale for an intermediate frame.
        var effectiveScale = _miniMapFloorScales.Resolve(imageKey, miniMapScale);
        effectiveScale = Math.Clamp(effectiveScale, 0d, 1d);
        if (effectiveScale == 0d)
        {
            _persistentMiniMap = new MapOverlayRenderMap(
                imagePath, 0, 0, 0, 0,
                anchors ?? (IReadOnlyList<MapOverlayRenderAnchor>)Array.Empty<MapOverlayRenderAnchor>(),
                null, annotations, floorLabel);
            _miniMapScale = 0d;
            _miniMapBaseScale = miniMapScale;
            _miniMapImageKey = imageKey;
            InvalidateLockedBackground();
            if (IsVisible)
                Present();
            return;
        }
        if (!MapOverlayBitmapRenderer.TryGetScaledImageSize(
                imagePath, effectiveScale, out var scaledWidth, out var scaledHeight))
        {
            _persistentMiniMap = null;
            _miniMapScale = null;
            _miniMapBaseScale = null;
            _miniMapImageKey = null;
            return;
        }
        if (_persistentMiniMap is not null
            && string.Equals(_persistentMiniMap.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(scaledWidth - _persistentMiniMap.Width) < 0.01f
            && Math.Abs(scaledHeight - _persistentMiniMap.Height) < 0.01f
            && string.Equals(_persistentMiniMap.FloorLabel, floorLabel, StringComparison.Ordinal)
            && _miniMapScale == effectiveScale)
        {
            // 小地图结构和尺寸完全未改变，保持现状，严禁盲目 InvalidateLockedBackground 导致 120ms 的全屏重绘
            return;
        }

        _persistentMiniMap = new MapOverlayRenderMap(
            imagePath, 0, 0, scaledWidth, scaledHeight,
            anchors ?? (IReadOnlyList<MapOverlayRenderAnchor>)Array.Empty<MapOverlayRenderAnchor>(),
            null, annotations, floorLabel);
        _miniMapScale = effectiveScale;
        _miniMapBaseScale = miniMapScale;
        _miniMapImageKey = imageKey;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }

    public void ClearPersistentMiniMap()
    {
        _persistentMiniMap = null;
        _miniMapScale = null;
        _miniMapBaseScale = null;
        _miniMapImageKey = null;
        RefreshVisibleContent();
    }

    public void ClearTemporaryMiniMapScales()
    {
        _miniMapFloorScales.Clear();
        if (_miniMapBaseScale is double baseScale)
            SetMiniMapScaleCore(baseScale);
    }

    private void SetMiniMapScaleCore(double scale)
    {
        if (_persistentMiniMap is not { } miniMap)
            return;
        scale = Math.Clamp(scale, 0d, 1d);
        if (scale == 0d)
        {
            _persistentMiniMap = miniMap with { Width = 0f, Height = 0f };
            _miniMapScale = 0d;
            InvalidateLockedBackground();
            if (IsVisible)
                Present();
            return;
        }
        if (!MapOverlayBitmapRenderer.TryGetScaledImageSize(
                miniMap.ImagePath, scale, out var width, out var height))
            return;

        _persistentMiniMap = miniMap with { Width = width, Height = height };
        _miniMapScale = scale;
        InvalidateLockedBackground();
        if (IsVisible)
            Present();
    }
}
