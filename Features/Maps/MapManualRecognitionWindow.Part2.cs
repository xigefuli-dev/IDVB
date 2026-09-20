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

    private void Render()
    {
        _canvas.Children.Clear();
        var viewport = GetViewportLocalBounds();
        var viewportFrame = new Rectangle
        {
            Width = viewport.Width,
            Height = viewport.Height,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 245, 74, 74)),
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(20, 245, 74, 74)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(viewportFrame, viewport.X);
        Canvas.SetTop(viewportFrame, viewport.Y);
        _canvas.Children.Add(viewportFrame);
        for (var index = 0; index < _selections.Count; index++)
            DrawSelection(_selections[index], index);
        if (_activeSelection is { } active)
            DrawSelection(active, _selections.Count);

        _instruction.Text = _selections.Count switch
        {
            0 => "手动识别：请先拖框蓝色“大门”图标\n右键或 Backspace 撤销，Esc 取消",
            1 => "已选择大门；请再拖框绿色“侧门”图标\n右键或 Backspace 撤销，Esc 取消",
            _ => "正在计算地图候选……"
        };
    }

    private void DrawSelection(Rect rectangle, int index)
    {
        var color = index == 0
            ? Color.FromArgb(255, 38, 133, 255)
            : Color.FromArgb(255, 63, 207, 123);
        var frame = new Rectangle
        {
            Width = rectangle.Width,
            Height = rectangle.Height,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 4,
            Fill = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(frame, rectangle.X);
        Canvas.SetTop(frame, rectangle.Y);
        _canvas.Children.Add(frame);
        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 15, 18, 24)),
            Padding = new Thickness(6, 3, 6, 3),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = index == 0 ? "大门" : "侧门",
                Foreground = new SolidColorBrush(color),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            }
        };
        Canvas.SetLeft(label, rectangle.X);
        Canvas.SetTop(label, Math.Max(0d, rectangle.Y - 30d));
        _canvas.Children.Add(label);
    }

    private Rect GetViewportLocalBounds()
    {
        if (_canvas.ActualWidth <= 0d || _canvas.ActualHeight <= 0d)
            return Rect.Empty;
        return new Rect(
            ((_viewportBounds.X - _frame.ClientBounds.X) / _frame.ClientBounds.Width) * _canvas.ActualWidth,
            ((_viewportBounds.Y - _frame.ClientBounds.Y) / _frame.ClientBounds.Height) * _canvas.ActualHeight,
            (_viewportBounds.Width / _frame.ClientBounds.Width) * _canvas.ActualWidth,
            (_viewportBounds.Height / _frame.ClientBounds.Height) * _canvas.ActualHeight);
    }

    private Point ClampToViewport(Point point)
    {
        var viewport = GetViewportLocalBounds();
        return new Point(
            Math.Clamp(point.X, viewport.X, viewport.X + viewport.Width),
            Math.Clamp(point.Y, viewport.Y, viewport.Y + viewport.Height));
    }

    private bool IsLargeEnough(Rect rectangle)
    {
        var physicalWidth = rectangle.Width / _canvas.ActualWidth * _frame.ClientBounds.Width;
        var physicalHeight = rectangle.Height / _canvas.ActualHeight * _frame.ClientBounds.Height;
        return physicalWidth >= MinimumPhysicalSelectionSize
            && physicalHeight >= MinimumPhysicalSelectionSize;
    }

    private MapScreenRect ToScreenRect(Rect rectangle) => new(
        _frame.ClientBounds.X + (rectangle.X / _canvas.ActualWidth * _frame.ClientBounds.Width),
        _frame.ClientBounds.Y + (rectangle.Y / _canvas.ActualHeight * _frame.ClientBounds.Height),
        rectangle.Width / _canvas.ActualWidth * _frame.ClientBounds.Width,
        rectangle.Height / _canvas.ActualHeight * _frame.ClientBounds.Height);

    private void Complete(
        ManualGateSelectionResult? result,
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
}
