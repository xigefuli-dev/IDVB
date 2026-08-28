using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenCvSharp;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Display;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using XamlWindow = Microsoft.UI.Xaml.Window;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;
/// <summary>Interactive frozen-game selector used only while F4 manual recognition is active.</summary>
public sealed partial class MapManualRecognitionWindow
{

    private void RegisterCaptureProtection()
    {
        if (_captureProtection is null || _window is null)
            return;
        try
        {
            _captureProtectionRegistration = _captureProtection.RegisterWindow(
                WindowNative.GetWindowHandle(_window),
                CaptureProtectionWindowCategory.DisplayLayer,
                "手动识别窗口");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[ManualRecognition] 捕获保护登记失败：{exception.Message}");
        }
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(_canvas);
        if (current.Properties.IsRightButtonPressed)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (!current.Properties.IsLeftButtonPressed || _selections.Count >= 2)
            return;
        var point = current.Position;
        if (!GetViewportLocalBounds().Contains(point))
            return;
        _dragStart = ClampToViewport(point);
        _activeSelection = new Rect(_dragStart.Value, _dragStart.Value);
        _canvas.CapturePointer(e.Pointer);
        Render();
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null)
            return;
        _activeSelection = CreateRect(
            _dragStart.Value,
            ClampToViewport(e.GetCurrentPoint(_canvas).Position));
        Render();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null)
            return;
        var rectangle = CreateRect(
            _dragStart.Value,
            ClampToViewport(e.GetCurrentPoint(_canvas).Position));
        _dragStart = null;
        _activeSelection = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        if (IsLargeEnough(rectangle))
            _selections.Add(rectangle);
        Render();
        e.Handled = true;
        if (_selections.Count == 2)
        {
            Complete(
                new ManualGateSelectionResult(
                    ToScreenRect(_selections[0]),
                    ToScreenRect(_selections[1])));
        }
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = null;
        _activeSelection = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        Render();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
        else if (e.Key == VirtualKey.Back)
        {
            e.Handled = true;
            Undo();
        }
    }

    private void Undo()
    {
        _dragStart = null;
        _activeSelection = null;
        if (_selections.Count > 0)
            _selections.RemoveAt(_selections.Count - 1);
        Render();
    }
}
