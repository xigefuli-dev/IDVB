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
public sealed class MapManualTransformWindow
{
    private const double MinimumScale = 0.06d;
    private const double MaximumScale = 20d;
    private const double ZoomStep = 1.1d;
    private const double HandleSizeDip = 10d;
    private const double HandleHitTolerancePhysical = 9d;

    private readonly CapturedGameFrame _frame;
    private readonly RuntimeMapRecognition _recognition;
    private readonly MapOverlayTransform _initialTransform;
    private readonly TaskCompletionSource<MapOverlayTransform?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private XamlWindow? _window;
    private Canvas? _canvas;
    private Image? _mapImage;
    private Rectangle? _boundaryBox;
    private readonly List<Rectangle> _handles = [];

    private double _scale;
    private double _offsetX;
    private double _offsetY;

    // Pan gesture.
    private Point? _dragStartPointer;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;

    // Handle-scale gesture (keeps the map center anchored).
    private Point? _resizeStartPointer;
    private double _resizeStartDistance;
    private double _resizeStartScale;
    private double _resizeCenterX;
    private double _resizeCenterY;
    private bool _completed;
    private readonly ICaptureProtectionService? _captureProtection;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;

    private MapManualTransformWindow(
        CapturedGameFrame frame,
        RuntimeMapRecognition recognition,
        MapOverlayTransform initialTransform,
        ICaptureProtectionService? captureProtection)
    {
        _frame = frame;
        _recognition = recognition;
        _initialTransform = initialTransform;
        _captureProtection = captureProtection;
        _scale = initialTransform.ScaleX;
        _offsetX = initialTransform.OffsetX;
        _offsetY = initialTransform.OffsetY;
    }

    public static async Task<MapOverlayTransform?> ShowAsync(
        CapturedGameFrame frame,
        RuntimeMapRecognition recognition,
        MapOverlayTransform initialTransform,
        CancellationToken cancellationToken,
        ICaptureProtectionService? captureProtection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recognition.Result.OverlayTransform is null
            || !File.Exists(recognition.FloorImagePath))
        {
            return null;
        }
        var window = new MapManualTransformWindow(
            frame,
            recognition,
            initialTransform,
            captureProtection);
        return await window.ShowCoreAsync(cancellationToken);
    }

    private async Task<MapOverlayTransform?> ShowCoreAsync(
        CancellationToken cancellationToken)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(64, 6, 10, 16)),
            IsTabStop = true
        };
        var canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255))
        };
        _canvas = canvas;
        var mapImage = new Image
        {
            Source = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(_recognition.FloorImagePath)
            },
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        _mapImage = mapImage;
        canvas.Children.Add(mapImage);
        var boundaryBox = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 120, 190, 255)),
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        _boundaryBox = boundaryBox;
        canvas.Children.Add(boundaryBox);
        for (var i = 0; i < 8; i++)
        {
            var handle = new Rectangle
            {
                Width = HandleSizeDip,
                Height = HandleSizeDip,
                Fill = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 70, 130, 255)),
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _handles.Add(handle);
            canvas.Children.Add(handle);
        }
        root.Children.Add(canvas);

        var instruction = new TextBlock
        {
            Text = "拖动地图调整位置 · 拖动手柄或滚轮调整缩放 · 回车确认 · Esc 取消",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        };
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 15, 18, 24)),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18),
            Child = instruction
        };
        root.Children.Add(header);

        var confirmButton = new Button
        {
            Content = "确认缩放",
            MinWidth = 96,
            MinHeight = 36,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18)
        };
        confirmButton.Click += (_, _) => Complete(BuildResult());
        root.Children.Add(confirmButton);

        root.Loaded += (_, _) =>
        {
            root.Focus(FocusState.Programmatic);
            Render();
        };
        root.SizeChanged += (_, _) => Render();
        root.KeyDown += Root_KeyDown;
        canvas.PointerPressed += Canvas_PointerPressed;
        canvas.PointerMoved += Canvas_PointerMoved;
        canvas.PointerReleased += Canvas_PointerReleased;
        canvas.PointerCanceled += Canvas_PointerCanceled;
        canvas.PointerWheelChanged += Canvas_PointerWheelChanged;

        _window = new XamlWindow
        {
            Content = root,
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
        RegisterCaptureProtection();
        // 消除 WinUI 默认白色底色：将窗口设为分层半透明
        var hwnd = WindowNative.GetWindowHandle(_window);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int LWA_ALPHA = 0x2;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        _ = SetLayeredWindowAttributes(hwnd, 0, 205, LWA_ALPHA);
        using var cancellationRegistration = cancellationToken.Register(
            () => CompleteOnDispatcher(dispatcher));
        try
        {
            return await _completion.Task;
        }
        finally
        {
            _captureProtectionRegistration?.Dispose();
            _captureProtectionRegistration = null;
            _window = null;
            root.Children.Clear();
            canvas.Children.Clear();
        }
    }

    private void RegisterCaptureProtection()
    {
        if (_captureProtection is null || _window is null)
            return;
        try
        {
            _captureProtectionRegistration = _captureProtection.RegisterWindow(
                WindowNative.GetWindowHandle(_window),
                CaptureProtectionWindowCategory.DisplayLayer,
                "手动缩放确认窗口");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[ManualTransform] 捕获保护登记失败：{exception.Message}");
        }
    }

    // ── 换算：物理像素 ↔ Canvas 逻辑像素（DPI 无关）──

    private double CanvasWidth => _canvas?.ActualWidth ?? 0d;
    private double CanvasHeight => _canvas?.ActualHeight ?? 0d;

    private Point ToCanvas(Point physical)
    {
        var bounds = _frame.ClientBounds;
        return new Point(
            bounds.Width > 0
                ? (physical.X - bounds.X) / bounds.Width * CanvasWidth
                : 0d,
            bounds.Height > 0
                ? (physical.Y - bounds.Y) / bounds.Height * CanvasHeight
                : 0d);
    }

    private Point ToPhysical(Point canvas)
    {
        var bounds = _frame.ClientBounds;
        return new Point(
            CanvasWidth > 0
                ? bounds.X + canvas.X / CanvasWidth * bounds.Width
                : bounds.X,
            CanvasHeight > 0
                ? bounds.Y + canvas.Y / CanvasHeight * bounds.Height
                : bounds.Y);
    }

    // ── 地图几何（物理像素）──

    private double MapWidth =>
        _initialTransform.ReferenceWidth * _scale;
    private double MapHeight =>
        _initialTransform.ReferenceHeight * _scale;
    private double MapCenterX => _offsetX + MapWidth / 2d;
    private double MapCenterY => _offsetY + MapHeight / 2d;

    private Point[] GetHandlePositions()
    {
        var left = _offsetX;
        var top = _offsetY;
        var right = _offsetX + MapWidth;
        var bottom = _offsetY + MapHeight;
        var centerX = MapCenterX;
        var centerY = MapCenterY;
        return
        [
            new Point(left, top),
            new Point(centerX, top),
            new Point(right, top),
            new Point(right, centerY),
            new Point(right, bottom),
            new Point(centerX, bottom),
            new Point(left, bottom),
            new Point(left, centerY)
        ];
    }

    private int HitHandle(Point physical)
    {
        var positions = GetHandlePositions();
        for (var i = 0; i < positions.Length; i++)
        {
            if (Distance(physical, positions[i]) <= HandleHitTolerancePhysical)
                return i;
        }
        return -1;
    }

    // ── 指针交互 ──

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var canvasPoint = e.GetCurrentPoint(_canvas!).Position;
        var physicalPoint = ToPhysical(canvasPoint);
        if (HitHandle(physicalPoint) >= 0)
        {
            var distance = Distance(physicalPoint, new Point(MapCenterX, MapCenterY));
            if (distance < 1d)
                return;
            _resizeStartPointer = canvasPoint;
            _resizeStartDistance = distance;
            _resizeStartScale = _scale;
            _resizeCenterX = MapCenterX;
            _resizeCenterY = MapCenterY;
            _canvas!.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        _dragStartPointer = canvasPoint;
        _dragStartOffsetX = _offsetX;
        _dragStartOffsetY = _offsetY;
        _canvas!.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeStartPointer is not null)
        {
            var canvasPoint = e.GetCurrentPoint(_canvas!).Position;
            var physicalPoint = ToPhysical(canvasPoint);
            var distance = Distance(
                physicalPoint,
                new Point(_resizeCenterX, _resizeCenterY));
            if (distance < 1d)
                return;
            var newScale = Math.Clamp(
                _resizeStartScale * (distance / _resizeStartDistance),
                MinimumScale,
                MaximumScale);
            _scale = newScale;
            _offsetX = _resizeCenterX - MapWidth / 2d;
            _offsetY = _resizeCenterY - MapHeight / 2d;
            Render();
            e.Handled = true;
            return;
        }
        if (_dragStartPointer is { } dragStart)
        {
            var canvasPoint = e.GetCurrentPoint(_canvas!).Position;
            var currentPhysical = ToPhysical(canvasPoint);
            var startPhysical = ToPhysical(dragStart);
            _offsetX = _dragStartOffsetX + (currentPhysical.X - startPhysical.X);
            _offsetY = _dragStartOffsetY + (currentPhysical.Y - startPhysical.Y);
            Render();
            e.Handled = true;
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartPointer is null && _resizeStartPointer is null)
            return;
        _dragStartPointer = null;
        _resizeStartPointer = null;
        _canvas!.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragStartPointer = null;
        _resizeStartPointer = null;
        _canvas!.ReleasePointerCapture(e.Pointer);
    }

    private void Canvas_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas!);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        var physicalPoint = ToPhysical(point.Position);
        var newScale = Math.Clamp(
            _scale * (delta > 0 ? ZoomStep : 1d / ZoomStep),
            MinimumScale,
            MaximumScale);
        var fractionX = MapWidth > 0
            ? (physicalPoint.X - _offsetX) / MapWidth
            : 0d;
        var fractionY = MapHeight > 0
            ? (physicalPoint.Y - _offsetY) / MapHeight
            : 0d;
        _offsetX = physicalPoint.X - fractionX
            * (_initialTransform.ReferenceWidth * newScale);
        _offsetY = physicalPoint.Y - fractionY
            * (_initialTransform.ReferenceHeight * newScale);
        _scale = newScale;
        Render();
        e.Handled = true;
    }

    // ── 渲染 ──

    private void Render()
    {
        if (_canvas is not { } canvas
            || _mapImage is not { } mapImage
            || _boundaryBox is not { } box
            || CanvasWidth <= 0d
            || CanvasHeight <= 0d)
        {
            return;
        }
        var topLeft = ToCanvas(new Point(_offsetX, _offsetY));
        var width = MapWidth / _frame.ClientBounds.Width * CanvasWidth;
        var height = MapHeight / _frame.ClientBounds.Height * CanvasHeight;

        mapImage.Width = width;
        mapImage.Height = height;
        Canvas.SetLeft(mapImage, topLeft.X);
        Canvas.SetTop(mapImage, topLeft.Y);
        box.Width = width;
        box.Height = height;
        Canvas.SetLeft(box, topLeft.X);
        Canvas.SetTop(box, topLeft.Y);

        var positions = GetHandlePositions();
        var offset = HandleSizeDip / 2d;
        for (var i = 0; i < _handles.Count && i < positions.Length; i++)
        {
            var handlePoint = ToCanvas(positions[i]);
            Canvas.SetLeft(_handles[i], handlePoint.X - offset);
            Canvas.SetTop(_handles[i], handlePoint.Y - offset);
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
        else if (e.Key == VirtualKey.Enter
            || e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            Complete(BuildResult());
        }
    }

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
/*
 * 文件职责：MapManualTransformWindow。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
