using System.Drawing;

namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    private readonly MapOverlayNativeWindow _mapNativeWindow;
    private bool _mapLayerDirty = true;
    private int _mapLayerPixelWidth;
    private int _mapLayerPixelHeight;
    private long _mapLayerBitmapBuildCount;
    private long _mapLayerTransformMoveCount;

    public bool IsCaptureExclusionEnabled => _nativeWindow.IsCaptureExclusionEnabled
        && (_map is null || _mapNativeWindow.IsCaptureExclusionEnabled);
    internal long MapLayerBitmapBuildCount => Interlocked.Read(ref _mapLayerBitmapBuildCount);
    internal long MapLayerTransformMoveCount => Interlocked.Read(ref _mapLayerTransformMoveCount);

    public void SetMapContentVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_showMapContent == visible)
            return;
        _showMapContent = visible;
        if (!visible)
            _mapNativeWindow.Hide();
        if (IsVisible)
            Present();
    }

    public bool TrySetCaptureExclusion(bool enabled, out string failureReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_nativeWindow.TrySetCaptureExclusion(enabled, out failureReason))
            return false;
        return _mapNativeWindow.TrySetCaptureExclusion(enabled, out failureReason);
    }

    public void Hide()
    {
        _mapNativeWindow.Hide();
        _nativeWindow.Hide();
    }

    private void PresentMapLayerOnly()
    {
        if (_map is not null && _showMainContent && _showMapContent)
            PresentMapLayer(_map, ResolveOverlayDpi());
        else
            _mapNativeWindow.Hide();
    }

    private void PresentMapLayer(MapOverlayRenderMap map, uint dpi)
    {
        var placement = ResolveMapLayerPlacement(map);
        if (!placement.Bounds.IsValid || !placement.ClipBounds.IsValid)
        {
            _mapNativeWindow.Hide();
            return;
        }

        var width = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Width);
        var height = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Height);
        if (_mapLayerDirty
            || !_mapNativeWindow.HasRetainedLayer(width, height)
            || _mapLayerPixelWidth != width
            || _mapLayerPixelHeight != height)
        {
            using var bitmap = MapOverlayBitmapRenderer.RenderMapLayer(
                map,
                dpi,
                _showGateMarkers && map.SupportsVectorRoutes,
                _showAuxiliaryAnchors,
                _showTextAnnotations && map.SupportsVectorRoutes,
                _showBoxAnnotations && map.SupportsVectorRoutes,
                _showLineAnnotations && map.SupportsVectorRoutes,
                _mapOpacity);
            _mapNativeWindow.Present(
                bitmap,
                placement.Bounds,
                placement.ClipBounds,
                _nativeWindow.Handle);
            _mapLayerDirty = false;
            _mapLayerPixelWidth = width;
            _mapLayerPixelHeight = height;
            Interlocked.Increment(ref _mapLayerBitmapBuildCount);
            return;
        }

        _mapNativeWindow.MoveRetainedLayer(
            placement.Bounds,
            placement.ClipBounds,
            _nativeWindow.Handle);
        Interlocked.Increment(ref _mapLayerTransformMoveCount);
    }

    private void MoveMapLayerOnly()
    {
        if (_map is null || !_showMainContent || !_showMapContent)
        {
            _mapNativeWindow.Hide();
            return;
        }

        var width = MapOverlayRealtimeTransformPlanner.RoundToPixel(_map.Width);
        var height = MapOverlayRealtimeTransformPlanner.RoundToPixel(_map.Height);
        if (_mapLayerDirty || !_mapNativeWindow.HasRetainedLayer(width, height))
        {
            PresentMapLayerOnly();
            return;
        }

        var placement = ResolveMapLayerPlacement(_map);
        if (!placement.Bounds.IsValid || !placement.ClipBounds.IsValid)
        {
            _mapNativeWindow.Hide();
            return;
        }

        _mapNativeWindow.MoveRetainedLayer(
            placement.Bounds,
            placement.ClipBounds,
            _nativeWindow.Handle);
        Interlocked.Increment(ref _mapLayerTransformMoveCount);
    }

    private MapLayerPlacement ResolveMapLayerPlacement(MapOverlayRenderMap map)
    {
        var bounds = new MapScreenRect(
            _gameBounds.X + map.Left,
            _gameBounds.Y + map.Top,
            MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Width),
            MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Height));
        var clip = _gameBounds;
        if (!_allowExtend && map.ClipBounds is { IsValid: true } viewport)
        {
            clip = Intersect(
                clip,
                new MapScreenRect(
                    _gameBounds.X + viewport.X,
                    _gameBounds.Y + viewport.Y,
                    viewport.Width,
                    viewport.Height));
        }
        return new MapLayerPlacement(bounds, clip);
    }

    private uint ResolveOverlayDpi()
    {
        var dpi = _gameWindowHandle == IntPtr.Zero ? 0u : GetDpiForWindow(_gameWindowHandle);
        return dpi == 0 ? 96u : dpi;
    }

    private static MapScreenRect Intersect(MapScreenRect left, MapScreenRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new MapScreenRect(x, y, Math.Max(0, rightEdge - x), Math.Max(0, bottomEdge - y));
    }

    private readonly record struct MapLayerPlacement(
        MapScreenRect Bounds,
        MapScreenRect ClipBounds);
}
