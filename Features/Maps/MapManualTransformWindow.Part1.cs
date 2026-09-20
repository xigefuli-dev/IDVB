// IDVB Remaster — 玩家决定缩放值的变换窗口
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;
using XamlWindow = Microsoft.UI.Xaml.Window;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Photoshop-style transform window shown after a successful scan when
/// "由玩家决定缩放值" is enabled. Drag inside the map to pan, drag a corner or
/// edge handle to fine-tune scale, or use the wheel to zoom. Enter confirms,
/// Esc cancels. The confirmed transform is rendered as-is (no CV re-alignment)
/// and cached as the highest-trust source.
///
/// All transform math runs in physical screen pixels (same convention as the
/// overlay renderer); canvas coordinates are only used for display and are
/// mapped through the canvas/clientBounds ratio so DPI scaling cannot skew the
/// preview against the final overlay.
/// </summary>
public sealed partial class MapManualTransformWindow
{

    private MapOverlayTransform BuildResult()
    {
        var source = _initialTransform;
        return new MapOverlayTransform
        {
            ScaleX = _scale,
            ScaleY = _scale,
            OffsetX = _offsetX,
            OffsetY = _offsetY,
            ReferenceCenterX = source.ReferenceCenterX,
            ReferenceCenterY = source.ReferenceCenterY,
            ScreenCenterX = source.ScreenCenterX,
            ScreenCenterY = source.ScreenCenterY,
            ReferenceWidth = source.ReferenceWidth,
            ReferenceHeight = source.ReferenceHeight,
            OrientationDegrees = source.OrientationDegrees,
            AlignmentMode = source.AlignmentMode,
            MaximumResidualPixels = source.MaximumResidualPixels,
            UsedDegenerateAxisFallback = source.UsedDegenerateAxisFallback
        };
    }

    private void Complete(
        MapOverlayTransform? result,
        bool closeWindow = true,
        bool restoreGameFocus = true)
    {
        if (_completed)
            return;
        _completed = true;
        var window = _window;
        _window = null;
        if (closeWindow && window is not null)
        {
            window.Content = null;
            window.Close();
        }
        _completion.TrySetResult(result);
        if (restoreGameFocus)
            SetForegroundWindow(_frame.WindowHandle);
    }

    private void CompleteOnDispatcher(DispatcherQueue dispatcher)
    {
        if (dispatcher.HasThreadAccess)
        {
            Complete(null, restoreGameFocus: false);
            return;
        }
        dispatcher.TryEnqueue(
            () => Complete(null, restoreGameFocus: false));
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static RectInt32 ToRectInt32(MapScreenRect rect) => new(
        (int)Math.Round(rect.X),
        (int)Math.Round(rect.Y),
        (int)Math.Round(rect.Width),
        (int)Math.Round(rect.Height));

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}
