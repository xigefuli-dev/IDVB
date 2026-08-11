using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

internal enum SurveyEditorTool
{
    Select,
    Pan,
    Decontaminate,
    Align,
    Eraser
}

internal enum SurveyEraseMode
{
    Eraser,
    Sandpaper
}

internal sealed class SurveyZoomChangedEventArgs : EventArgs
{
    public required double Percent { get; init; }
}

internal sealed class SurveyLayerToolEventArgs : EventArgs
{
    public required Guid LayerId { get; init; }
    public required SurveyEditorTool Tool { get; init; }
}

internal sealed class SurveyMaskStrokeEventArgs : EventArgs
{
    public required IReadOnlyList<SurveyWorldPoint> Points { get; init; }
}

internal sealed partial class SurveyCanvasView
{
    private readonly CompositeTransform _viewportTransform = new();
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;
    private bool _hasInitialFit;
    private bool _isMasking;
    private readonly List<SurveyWorldPoint> _maskPoints = [];
    private Shape? _brushPreview;
    private double _brushSize = 64d;
    private SurveyBrushShape _brushShape = SurveyBrushShape.Circle;

    public SurveyEditorTool ActiveTool { get; private set; } = SurveyEditorTool.Select;
    public double ZoomPercent => _viewportTransform.ScaleX * 100d;

    public event EventHandler<SurveyZoomChangedEventArgs>? ZoomChanged;
    public event EventHandler<SurveyLayerToolEventArgs>? LayerToolInvoked;
    public event EventHandler<SurveyMaskStrokeEventArgs>? MaskStrokeCommitted;

    public void SetTool(SurveyEditorTool tool)
    {
        ActiveTool = tool;
        _brushPreview?.SetValue(VisibilityProperty,
            tool == SurveyEditorTool.Eraser ? Visibility.Visible : Visibility.Collapsed);
        ProtectedCursor = tool switch
        {
            SurveyEditorTool.Pan => Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Hand),
            SurveyEditorTool.Eraser => Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Cross),
            _ => Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Arrow)
        };
    }

    public void SetBrush(double size, SurveyBrushShape shape)
    {
        _brushSize = Math.Clamp(size, 1d, 1024d);
        if (_brushShape != shape)
        {
            _brushShape = shape;
            RecreateBrushPreview();
        }
        UpdateBrushPreviewSize();
    }

    public void ChangeZoom(double multiplier) => SetZoomPercent(ZoomPercent * multiplier);

    public void SetZoomPercent(double percent)
    {
        var zoom = Math.Clamp(percent / 100d, 0.1d, 8d);
        var oldZoom = Math.Max(0.1d, _viewportTransform.ScaleX);
        var centerX = ActualWidth / 2d;
        var centerY = ActualHeight / 2d;
        var worldX = (centerX - _viewportTransform.TranslateX) / oldZoom;
        var worldY = (centerY - _viewportTransform.TranslateY) / oldZoom;
        _viewportTransform.ScaleX = zoom;
        _viewportTransform.ScaleY = zoom;
        _viewportTransform.TranslateX = centerX - (worldX * zoom);
        _viewportTransform.TranslateY = centerY - (worldY * zoom);
        RaiseZoomChanged();
    }

    public void FitToViewport()
    {
        if (_canvas.Width <= 0d || _canvas.Height <= 0d || ActualWidth <= 0d || ActualHeight <= 0d)
            return;
        var width = Math.Max(1d, ActualWidth - 24d);
        var height = Math.Max(1d, ActualHeight - 24d);
        var zoom = Math.Clamp(Math.Min(width / _canvas.Width, height / _canvas.Height), 0.1d, 8d);
        _viewportTransform.ScaleX = zoom;
        _viewportTransform.ScaleY = zoom;
        _viewportTransform.TranslateX = (ActualWidth - (_canvas.Width * zoom)) / 2d;
        _viewportTransform.TranslateY = (ActualHeight - (_canvas.Height * zoom)) / 2d;
        RaiseZoomChanged();
    }

    public void FitAfterNextLayout()
    {
        _hasInitialFit = true;
        DispatcherQueue.TryEnqueue(FitToViewport);
    }

    private void InitializeViewportInteractions()
    {
        _viewportTransform.ScaleX = 1d;
        _viewportTransform.ScaleY = 1d;
        _canvas.RenderTransformOrigin = new Point(0d, 0d);
        _canvas.RenderTransform = _viewportTransform;
        SizeChanged += (_, args) =>
        {
            Clip = new RectangleGeometry { Rect = new Rect(0d, 0d, args.NewSize.Width, args.NewSize.Height) };
        };
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Viewport_PointerPressed), true);
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Viewport_PointerMoved), true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Viewport_PointerReleased), true);
        AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Viewport_PointerCanceled), true);
        RecreateBrushPreview();
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
            return;
        if (ActiveTool == SurveyEditorTool.Pan)
        {
            _isPanning = true;
            _panStart = e.GetCurrentPoint(this).Position;
            _panStartX = _viewportTransform.TranslateX;
            _panStartY = _viewportTransform.TranslateY;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        if (ActiveTool != SurveyEditorTool.Eraser)
            return;
        _isMasking = true;
        _maskPoints.Clear();
        AddMaskPoint(e);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ActiveTool == SurveyEditorTool.Eraser)
            PositionBrushPreview(e.GetCurrentPoint(_canvas).Position);
        if (_isPanning)
        {
            var point = e.GetCurrentPoint(this).Position;
            _viewportTransform.TranslateX = _panStartX + point.X - _panStart.X;
            _viewportTransform.TranslateY = _panStartY + point.Y - _panStart.Y;
            e.Handled = true;
            return;
        }
        if (_isMasking)
        {
            AddMaskPoint(e);
            e.Handled = true;
        }
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            ReleasePointerCapture(e.Pointer);
            _isPanning = false;
            e.Handled = true;
        }
        if (!_isMasking)
            return;
        AddMaskPoint(e);
        ReleasePointerCapture(e.Pointer);
        _isMasking = false;
        if (_maskPoints.Count > 0)
        {
            MaskStrokeCommitted?.Invoke(this, new SurveyMaskStrokeEventArgs
            {
                Points = _maskPoints.ToArray()
            });
        }
        _maskPoints.Clear();
        e.Handled = true;
    }

    private void Viewport_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPanning = false;
        _isMasking = false;
        _maskPoints.Clear();
        ReleasePointerCapture(e.Pointer);
    }

    private void AddMaskPoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas).Position;
        PositionBrushPreview(point);
        var world = new SurveyWorldPoint(point.X - _originX, point.Y - _originY);
        if (_maskPoints.Count == 0
            || Distance(_maskPoints[^1], world) >= Math.Max(1d, _brushSize / 8d))
            _maskPoints.Add(world);
    }

    private void RecreateBrushPreview()
    {
        if (_brushPreview is not null)
            _canvas.Children.Remove(_brushPreview);
        _brushPreview = _brushShape == SurveyBrushShape.Circle
            ? new Ellipse()
            : new Rectangle();
        _brushPreview.Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        _brushPreview.Fill = new SolidColorBrush(Color.FromArgb(38, 255, 110, 80));
        _brushPreview.StrokeThickness = 1.5d;
        _brushPreview.IsHitTestVisible = false;
        _brushPreview.Visibility = ActiveTool == SurveyEditorTool.Eraser
            ? Visibility.Visible
            : Visibility.Collapsed;
        Canvas.SetZIndex(_brushPreview, 1_000_000);
        _canvas.Children.Add(_brushPreview);
        UpdateBrushPreviewSize();
    }

    private void UpdateBrushPreviewSize()
    {
        if (_brushPreview is null)
            return;
        _brushPreview.Width = _brushSize;
        _brushPreview.Height = _brushSize;
    }

    private void PositionBrushPreview(Point point)
    {
        if (_brushPreview is null)
            return;
        Canvas.SetLeft(_brushPreview, point.X - (_brushSize / 2d));
        Canvas.SetTop(_brushPreview, point.Y - (_brushSize / 2d));
    }

    private void RaiseZoomChanged() => ZoomChanged?.Invoke(
        this,
        new SurveyZoomChangedEventArgs { Percent = ZoomPercent });

    private void PreserveWorldViewportPosition(double originDeltaX, double originDeltaY)
    {
        _viewportTransform.TranslateX -= originDeltaX * _viewportTransform.ScaleX;
        _viewportTransform.TranslateY -= originDeltaY * _viewportTransform.ScaleY;
    }

    private static double Distance(SurveyWorldPoint left, SurveyWorldPoint right)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
