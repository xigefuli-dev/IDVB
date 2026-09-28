using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Owns the state of the non-activating, click-through runtime overlay.
/// Rendering is delegated to a native layered window so the game receives
/// mouse input through every transparent and painted pixel.
/// </summary>
public sealed partial class MapOverlayWindow : IDisposable
{

    private void EndPresentDeferral()
    {
        if (_presentDepth <= 0)
            return;
        _presentDepth--;
        if (_presentDepth != 0 || !_presentDirty)
            return;
        _presentDirty = false;
        PresentCore();
    }

    private static float ToFiniteSingle(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Overlay geometry is outside the supported range.");
        return (float)value;
    }

    private Bitmap RenderScene(MapOverlayRenderScene scene)
    {
        if (scene.Map is null)
            return MapOverlayBitmapRenderer.Render(scene);
        if (_lockedBackground is null
            || _lockedBackgroundWidth != scene.PixelWidth
            || _lockedBackgroundHeight != scene.PixelHeight
            || _lockedBackgroundDpi != scene.Dpi)
        {
            InvalidateLockedBackground();
            _lockedBackground = MapOverlayBitmapRenderer.Render(
                scene with
                {
                    Status = null,
                    ShowStatus = false,
                    ObservationRegion = null,
                    Player = null,
                    MiniMap = null
                });
            _lockedBackgroundWidth = scene.PixelWidth;
            _lockedBackgroundHeight = scene.PixelHeight;
            _lockedBackgroundDpi = scene.Dpi;
        }
        return MapOverlayBitmapRenderer.ComposeDynamic(
            _lockedBackground,
            scene);
    }

    private void InvalidateLockedBackground()
    {
        _lockedBackground?.Dispose();
        _lockedBackground = null;
        _lockedBackgroundWidth = 0;
        _lockedBackgroundHeight = 0;
        _lockedBackgroundDpi = 0;
    }

    private void InvalidateMapLayer()
    {
        _mapLayerDirty = true;
        _mapLayerPixelWidth = 0;
        _mapLayerPixelHeight = 0;
    }

    public void SetAllowExtend(bool allow)
    {
        _allowExtend = allow;
        if (IsVisible && _map is not null && _showMainContent)
            MoveMapLayerOnly();
    }

    public void SetMapOpacity(double opacity)
    {
        _mapOpacity = (float)opacity;
        InvalidateMapLayer();
        if (IsVisible)
            PresentMapLayerOnly();
    }

    public void SetHideMiniMap(bool hide)
    {
        _hideMiniMap = hide;
        if (IsVisible) Present();
    }

    public void SetRouteLineThickness(int level)
    {
        _routeLineThickness = Math.Clamp(level, 0, 3);
        InvalidateMapLayer();
        if (IsVisible)
            Present();
    }

    public void SetShowGateMarkers(bool show)
    {
        _showGateMarkers = show;
        InvalidateMapLayer();
        if (IsVisible)
            PresentMapLayerOnly();
    }

    public void SetShowAuxiliaryAnchors(bool show)
    {
        _showAuxiliaryAnchors = show;
        InvalidateMapLayer();
        if (IsVisible)
            PresentMapLayerOnly();
    }

    public void SetShowTextAnnotations(bool show)
    {
        _showTextAnnotations = show;
        InvalidateMapLayer();
        if (IsVisible)
            PresentMapLayerOnly();
    }

    public void SetShowBoxAnnotations(bool show)
    {
        _showBoxAnnotations = show;
        InvalidateMapLayer();
        if (IsVisible)
            PresentMapLayerOnly();
    }

    public void SetShowLineAnnotations(bool show)
    {
        _showLineAnnotations = show;
        InvalidateMapLayer();
        if (IsVisible)
            PresentMapLayerOnly();
    }

    public void SetShowGateMarkersOnMiniMap(bool show)
    {
        _showGateMarkersOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowAuxiliaryAnchorsOnMiniMap(bool show)
    {
        _showAuxiliaryAnchorsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowTextAnnotationsOnMiniMap(bool show)
    {
        _showTextAnnotationsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowBoxAnnotationsOnMiniMap(bool show)
    {
        _showBoxAnnotationsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowLineAnnotationsOnMiniMap(bool show)
    {
        _showLineAnnotationsOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetShowFloorOnMiniMap(bool show)
    {
        _showFloorOnMiniMap = show;
        if (IsVisible)
            Present();
    }

    public void SetStatusOpacity(double opacity)
    {
        _statusOpacity = (float)opacity;
        if (IsVisible)
            Present();
    }

    public void SetStatusScale(double scale)
    {
        _statusScale = (float)Math.Clamp(scale, 0d, 1d);
        if (IsVisible)
            Present();
    }

    public void SetStatusOffsetX(double offsetX)
    {
        _statusOffsetX = (float)Math.Clamp(offsetX, 0d, 1d);
        if (IsVisible)
            Present();
    }

    public void SetStatusOffsetY(double offsetY)
    {
        _statusOffsetY = (float)Math.Clamp(offsetY, 0d, 1d);
        if (IsVisible)
            Present();
    }

    public void SetMiniMapOpacity(double opacity)
    {
        _miniMapOpacity = (float)opacity;
        if (IsVisible)
            Present();
    }

    public void SetMiniMapOffsetX(double offsetX)
    {
        _miniMapOffsetX = (float)Math.Clamp(offsetX, 0d, 1d);
        if (IsVisible)
            Present();
    }

    public void SetMiniMapOffsetY(double offsetY)
    {
        _miniMapOffsetY = (float)Math.Clamp(offsetY, 0d, 1d);
        if (IsVisible)
            Present();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        InvalidateLockedBackground();
        _mapNativeWindow.Dispose();
        _nativeWindow.Dispose();
        _map = null;
        _player = null;
        _mapId = Guid.Empty;
        _mapFloorKey = string.Empty;
        _mapImagePath = string.Empty;
        _mapUpdatedAt = default;
        _status = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private sealed class PresentLease(MapOverlayWindow owner) : IDisposable
    {
        private MapOverlayWindow? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndPresentDeferral();
        }
    }
}
