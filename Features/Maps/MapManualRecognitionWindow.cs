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

namespace IDVBuff.Features.Maps;

public sealed record ManualGateSelectionResult(
    MapScreenRect MainGateBounds,
    MapScreenRect SideGateBounds);

/// <summary>Interactive frozen-game selector used only while F4 manual recognition is active.</summary>
public sealed class MapManualRecognitionWindow
{
    private const double MinimumPhysicalSelectionSize = 6d;
    private readonly CapturedGameFrame _frame;
    private readonly MapScreenRect _viewportBounds;
    private readonly TaskCompletionSource<ManualGateSelectionResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _root = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 8, 12, 18)),
        IsTabStop = true
    };
    private readonly Canvas _canvas = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255))
    };
    private readonly TextBlock _instruction = new()
    {
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly List<Rect> _selections = [];
    private XamlWindow? _window;
    private Point? _dragStart;
    private Rect? _activeSelection;
    private bool _completed;

    private MapManualRecognitionWindow(
        CapturedGameFrame frame,
        MapScreenRect viewportBounds)
    {
        _frame = frame;
        _viewportBounds = viewportBounds;
    }

    public static async Task<ManualGateSelectionResult?> ShowAsync(
        CapturedGameFrame frame,
        MapScreenRect viewportBounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selector = new MapManualRecognitionWindow(frame, viewportBounds);
        return await selector.ShowCoreAsync(cancellationToken);
    }

    private async Task<ManualGateSelectionResult?> ShowCoreAsync(
        CancellationToken cancellationToken)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var image = new Image
        {
            Source = await CreateBitmapAsync(_frame.Image),
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        cancellationToken.ThrowIfCancellationRequested();
        _root.Children.Add(image);
        _root.Children.Add(_canvas);
        var instructions = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 15, 18, 24)),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(7),
            MaxWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18),
            Child = _instruction
        };
        _root.Children.Add(instructions);
        _root.Loaded += (_, _) =>
        {
            _root.Focus(FocusState.Programmatic);
            Render();
        };
        _root.SizeChanged += (_, _) => Render();
        _root.KeyDown += Root_KeyDown;
        _canvas.PointerPressed += Canvas_PointerPressed;
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;
        _canvas.PointerCanceled += Canvas_PointerCanceled;

        _window = new XamlWindow
        {
            Content = _root,
            ExtendsContentIntoTitleBar = true,
            SystemBackdrop = new TransparentBackdrop()
        };
        _window.Closed += (_, _) => Complete(null, closeWindow: false);
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }
        _window.AppWindow.MoveAndResize(ToRectInt32(_frame.ClientBounds));
        _window.Activate();
        using var cancellationRegistration = cancellationToken.Register(
            () => CompleteOnDispatcher(dispatcher));
        try
        {
            return await _completion.Task;
        }
        finally
        {
            // Complete() detaches the content before closing the WinUI window.
            // If the user closed the window directly, the Closed handler also
            // clears _window so this block never touches an already-closed
            // DesktopWindow object.
            _window = null;
            _root.Children.Clear();
            _canvas.Children.Clear();
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

    private static Rect CreateRect(Point start, Point end) => new(
        Math.Min(start.X, end.X),
        Math.Min(start.Y, end.Y),
        Math.Abs(end.X - start.X),
        Math.Abs(end.Y - start.Y));

    internal static async Task<BitmapImage> CreateBitmapAsync(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static RectInt32 ToRectInt32(MapScreenRect rect) => new(
        (int)Math.Round(rect.X),
        (int)Math.Round(rect.Y),
        (int)Math.Round(rect.Width),
        (int)Math.Round(rect.Height));

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}

/// <summary>In-game top-three chooser shown when manual geometry remains ambiguous.</summary>
public sealed class MapManualCandidateWindow
{
    private readonly CapturedGameFrame _frame;
    private readonly IReadOnlyList<MapRecognitionChoice> _choices;
    private readonly string _reason;
    private readonly TaskCompletionSource<int?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private XamlWindow? _window;
    private bool _completed;

    private MapManualCandidateWindow(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        string reason)
    {
        _frame = frame;
        _choices = choices;
        _reason = reason;
    }

    public static async Task<int?> ShowAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        string reason,
        CancellationToken cancellationToken)
    {
        if (choices.Count == 0)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        var chooser = new MapManualCandidateWindow(frame, choices, reason);
        return await chooser.ShowCoreAsync(cancellationToken);
    }

    private async Task<int?> ShowCoreAsync(
        CancellationToken cancellationToken)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var displayArea = DisplayArea.Primary;
        var workArea = displayArea.WorkArea;

        var columns = Math.Min(_choices.Count, 3);
        var rows = (_choices.Count + columns - 1) / columns;
        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 6, 10, 16)),
            IsTabStop = true,
            RowSpacing = 8,
            ColumnSpacing = 8,
            Padding = new Thickness(24, 18, 24, 24)
        };
        for (var c = 0; c < 3; c++)
            root.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        for (var r = 0; r < rows; r++)
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text = "请选择正确地图",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{_reason}\n可点击候选或按 1–{_choices.Count}；Esc 取消。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 210, 210, 210))
        });
        Grid.SetRow(header, 0);
        Grid.SetColumnSpan(header, 3);
        root.Children.Add(header);

        for (var i = 0; i < _choices.Count; i++)
        {
            var choiceIndex = i;
            var choice = _choices[i];
            var cell = CreateChoiceCell(choice, i);
            var button = new Button
            {
                Content = cell,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
            };
            button.Click += (_, _) => Complete(choiceIndex);
            var row = i / 3 + 1;
            var col = i % 3;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, col);
            root.Children.Add(button);
        }

        root.Loaded += (_, _) => root.Focus(FocusState.Programmatic);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Complete(null);
                return;
            }
            var index = e.Key switch
            {
                VirtualKey.Number1 or VirtualKey.NumberPad1 => 0,
                VirtualKey.Number2 or VirtualKey.NumberPad2 => 1,
                VirtualKey.Number3 or VirtualKey.NumberPad3 => 2,
                VirtualKey.Number4 or VirtualKey.NumberPad4 => 3,
                VirtualKey.Number5 or VirtualKey.NumberPad5 => 4,
                VirtualKey.Number6 or VirtualKey.NumberPad6 => 5,
                VirtualKey.Number7 or VirtualKey.NumberPad7 => 6,
                VirtualKey.Number8 or VirtualKey.NumberPad8 => 7,
                VirtualKey.Number9 or VirtualKey.NumberPad9 => 8,
                _ => -1
            };
            if (index >= 0 && index < _choices.Count)
            {
                e.Handled = true;
                Complete(index);
            }
        };

        _window = new XamlWindow
        {
            Content = root,
            ExtendsContentIntoTitleBar = true
        };
        _window.Closed += (_, _) => Complete(null, closeWindow: false);
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }
        _window.AppWindow.MoveAndResize(displayArea.OuterBounds);
        _window.Activate();
        // 消除 WinUI 默认白色底色：将窗口设为分层半透明
        var hwnd = WindowNative.GetWindowHandle(_window);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int LWA_ALPHA = 0x2;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        _ = SetLayeredWindowAttributes(hwnd, 0, 230, LWA_ALPHA);
        using var cancellationRegistration = cancellationToken.Register(
            () => CompleteOnDispatcher(dispatcher));
        try
        {
            return await _completion.Task;
        }
        finally
        {
            _window = null;
            root.Children.Clear();
        }
    }

    private static UIElement CreateChoiceCell(
        MapRecognitionChoice choice,
        int index)
    {
        var grid = new Grid();
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4)
        };
        if (File.Exists(choice.Recognition.FloorImagePath))
        {
            image.Source = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(choice.Recognition.FloorImagePath)
            };
        }
        grid.Children.Add(image);

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 8, 12, 18)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(10, 5, 10, 6)
        };
        var details = new StackPanel { Spacing = 2 };
        details.Children.Add(new TextBlock
        {
            Text = $"{index + 1}. {choice.Recognition.Map.DisplayName}",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        });
        details.Children.Add(new TextBlock
        {
            Text = $"几何误差 {choice.VectorError:F3} · 置信度 {choice.RawConfidence:P0}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 200, 200))
        });
        overlay.Child = details;
        grid.Children.Add(overlay);

        return grid;
    }

    private void Complete(
        int? result,
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
