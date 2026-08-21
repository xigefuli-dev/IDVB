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
    VignetteCorrection,
    Align,
    NormalizeColors,
    Template,
    Eraser,
    PaintBucket,
    Brush,
    Eyedropper
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

internal sealed class SurveyLayerPixelSampleEventArgs : EventArgs
{
    public required Guid LayerId { get; init; }
    public required int PixelX { get; init; }
    public required int PixelY { get; init; }
    public required Point ViewportPoint { get; init; }
    public required SurveyWorldPoint WorldPoint { get; init; }
}

internal sealed class SurveyMaskStrokeEventArgs : EventArgs
{
    public required IReadOnlyList<SurveyWorldPoint> Points { get; init; }
}

internal sealed class SurveyColorStrokeEventArgs : EventArgs
{
    public required Guid LayerId { get; init; }
    public required IReadOnlyList<SurveyWorldPoint> Points { get; init; }
}

internal sealed class SurveyColorFillEventArgs : EventArgs
{
    public required Guid LayerId { get; init; }
    public required int PixelX { get; init; }
    public required int PixelY { get; init; }
}

internal sealed partial class SurveyCanvasView
{
    private readonly CompositeTransform _viewportTransform = new();
    private bool _isPanning;
    private bool _panIsTemporary;
    private bool _temporaryNavigationActive;
    private bool _spaceIsDown;
    private Microsoft.UI.Xaml.Input.Pointer? _panPointer;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;
    private bool _hasInitialFit;
    private bool _isMasking;
    private readonly List<SurveyWorldPoint> _maskPoints = [];
    private Shape? _brushPreview;
    private readonly List<Shape> _liveMaskPreview = [];
    private double _brushSize = 64d;
    private SurveyBrushShape _brushShape = SurveyBrushShape.Circle;
    private SurveyColor _paintPreviewColor = new(220, 60, 45);
    private Guid? _colorStrokeLayerId;
    private readonly List<SurveyWorldPoint> _colorStrokePoints = [];
    private bool _templateColorSamplerArmed;
    private bool _pointerInsideCanvas;
    private double _wheelAccumulator;

    public SurveyEditorTool ActiveTool { get; private set; } = SurveyEditorTool.Select;
    public double ZoomPercent => _viewportTransform.ScaleX * 100d;

    public event EventHandler<SurveyZoomChangedEventArgs>? ZoomChanged;
    public event EventHandler<SurveyLayerToolEventArgs>? LayerToolInvoked;
    public event EventHandler<SurveyLayerPixelSampleEventArgs>? LayerPixelSampleRequested;
    public event EventHandler<SurveyMaskStrokeEventArgs>? MaskStrokeCommitted;
    public event EventHandler<SurveyColorStrokeEventArgs>? ColorStrokeCommitted;
    public event EventHandler<SurveyColorFillEventArgs>? ColorFillRequested;

    public void SetTool(SurveyEditorTool tool)
    {
        EndTemporaryNavigation();
        _colorStrokeLayerId = null;
        _colorStrokePoints.Clear();
        ClearLiveMaskPreview();
        ActiveTool = tool;
        _templateColorSamplerArmed = false;
        UpdateBrushPreviewAppearance();
        if (tool != SurveyEditorTool.Select)
            CancelTransformBoxInteraction(commit: false);
        UpdatePointerVisuals();
        UpdateTransformBox();
    }

    public void ArmTemplateColorSampler()
    {
        if (ActiveTool != SurveyEditorTool.Template)
            return;
        _templateColorSamplerArmed = true;
        UpdatePointerVisuals();
    }

    public void DisarmTemplateColorSampler()
    {
        _templateColorSamplerArmed = false;
        UpdatePointerVisuals();
    }

    public void SetBrush(double size, SurveyBrushShape shape)
    {
        _brushSize = Math.Clamp(Math.Round(size), 1d, 1024d);
        if (_brushShape != shape)
        {
            _brushShape = shape;
            RecreateBrushPreview();
        }
        UpdateBrushPreviewSize();
    }

    public void SetPaintColor(SurveyColor color)
    {
        _paintPreviewColor = color;
        UpdateBrushPreviewAppearance();
    }

    public void ClearMaskPreview() => ClearLiveMaskPreview();

    public void ChangeZoom(double multiplier) => SetZoomPercent(ZoomPercent * multiplier);

    public void BeginTemporaryNavigation()
    {
        if (_disposed || _spaceIsDown)
            return;
        _spaceIsDown = true;
        _temporaryNavigationActive = true;
        UpdatePointerVisuals();
    }

    public void EndTemporaryNavigation()
    {
        _spaceIsDown = false;
        _temporaryNavigationActive = false;
        if (_isPanning && _panIsTemporary)
        {
            ReleasePanPointer();
            _isPanning = false;
            _panIsTemporary = false;
        }
        UpdatePointerVisuals();
    }

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
        UpdateTransformBox();
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
        UpdateTransformBox();
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
            UpdateTransformBox();
        };
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Viewport_PointerPressed), true);
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Viewport_PointerMoved), true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Viewport_PointerReleased), true);
        AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Viewport_PointerCanceled), true);
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewport_PointerWheelChanged), true);
        _canvas.PointerEntered += Viewport_PointerEntered;
        _canvas.PointerExited += Viewport_PointerExited;
        LostFocus += Viewport_LostFocus;
        RecreateBrushPreview();
    }

    private void Viewport_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInsideCanvas = true;
        UpdatePointerVisuals();
    }

    private void Viewport_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInsideCanvas = false;
        UpdatePointerVisuals();
    }

    private void Viewport_LostFocus(object sender, RoutedEventArgs e) => EndTemporaryNavigation();

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (point.X < 0d || point.Y < 0d || point.X > ActualWidth || point.Y > ActualHeight)
            return;
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        e.Handled = true;
        _wheelAccumulator += delta / 120d;
        var steps = _wheelAccumulator;
        if (Math.Abs(steps) < 0.01d)
            return;
        _wheelAccumulator = 0d;
        ZoomAtPoint(point, Math.Pow(1.1d, steps));
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed)
            return;
        if (_temporaryNavigationActive)
        {
            BeginPan(e, temporary: true);
            return;
        }
        if (IsCompositeColorSamplerActive)
        {
            RequestCompositeColorSample(e);
            return;
        }
        if (ActiveTool == SurveyEditorTool.Pan)
        {
            BeginPan(e, temporary: false);
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
        var canvasPoint = e.GetCurrentPoint(_canvas).Position;
        _pointerInsideCanvas = IsInsideCanvas(canvasPoint);
        if (ActiveTool is SurveyEditorTool.Eraser or SurveyEditorTool.Brush
            && _pointerInsideCanvas)
            PositionBrushPreview(canvasPoint);
        UpdatePointerVisuals();
        if (_isPanning)
        {
            var point = e.GetCurrentPoint(this).Position;
            _viewportTransform.TranslateX = _panStartX + point.X - _panStart.X;
            _viewportTransform.TranslateY = _panStartY + point.Y - _panStart.Y;
            UpdateTransformBox();
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
            ReleasePanPointer();
            _isPanning = false;
            _panIsTemporary = false;
            UpdatePointerVisuals();
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
        EndTemporaryNavigation();
        _isPanning = false;
        _panIsTemporary = false;
        _isMasking = false;
        _colorStrokeLayerId = null;
        _colorStrokePoints.Clear();
        _maskPoints.Clear();
        ClearLiveMaskPreview();
        ReleasePanPointer();
        ReleasePointerCapture(e.Pointer);
        UpdatePointerVisuals();
    }

    private void BeginPan(PointerRoutedEventArgs e, bool temporary)
    {
        _isPanning = true;
        _panIsTemporary = temporary;
        _panPointer = e.Pointer;
        _panStart = e.GetCurrentPoint(this).Position;
        _panStartX = _viewportTransform.TranslateX;
        _panStartY = _viewportTransform.TranslateY;
        CapturePointer(e.Pointer);
        UpdatePointerVisuals();
        e.Handled = true;
    }

    private bool IsCompositeColorSamplerActive =>
        ActiveTool == SurveyEditorTool.Eyedropper
        || ActiveTool == SurveyEditorTool.Template && _templateColorSamplerArmed;

    private void RequestCompositeColorSample(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (!TryGetWorldPoint(point, out var worldPoint))
        {
            e.Handled = true;
            return;
        }
        LayerPixelSampleRequested?.Invoke(this, new SurveyLayerPixelSampleEventArgs
        {
            LayerId = Guid.Empty,
            PixelX = (int)Math.Floor(worldPoint.X),
            PixelY = (int)Math.Floor(worldPoint.Y),
            ViewportPoint = point,
            WorldPoint = worldPoint
        });
        if (ActiveTool == SurveyEditorTool.Template)
        {
            _templateColorSamplerArmed = false;
            UpdatePointerVisuals();
        }
        e.Handled = true;
    }

    private bool TryGetWorldPoint(Point viewportPoint, out SurveyWorldPoint worldPoint)
    {
        var zoom = _viewportTransform.ScaleX;
        if (!double.IsFinite(zoom) || zoom <= 0d)
        {
            worldPoint = default;
            return false;
        }
        var canvasX = (viewportPoint.X - _viewportTransform.TranslateX) / zoom;
        var canvasY = (viewportPoint.Y - _viewportTransform.TranslateY) / zoom;
        worldPoint = new SurveyWorldPoint(canvasX - _originX, canvasY - _originY);
        return double.IsFinite(worldPoint.X) && double.IsFinite(worldPoint.Y);
    }

    private void ReleasePanPointer()
    {
        if (_panPointer is not null)
            ReleasePointerCapture(_panPointer);
        _panPointer = null;
    }

    private void ZoomAtPoint(Point pointer, double multiplier)
    {
        var oldZoom = Math.Max(0.1d, _viewportTransform.ScaleX);
        var requestedZoom = Math.Clamp(oldZoom * multiplier, 0.1d, 8d);
        var worldX = (pointer.X - _viewportTransform.TranslateX) / oldZoom;
        var worldY = (pointer.Y - _viewportTransform.TranslateY) / oldZoom;
        _viewportTransform.ScaleX = requestedZoom;
        _viewportTransform.ScaleY = requestedZoom;
        _viewportTransform.TranslateX = pointer.X - (worldX * requestedZoom);
        _viewportTransform.TranslateY = pointer.Y - (worldY * requestedZoom);
        UpdateTransformBox();
        RaiseZoomChanged();
    }

    private void AddMaskPoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas).Position;
        PositionBrushPreview(point);
        var world = new SurveyWorldPoint(point.X - _originX, point.Y - _originY);
        if (_maskPoints.Count == 0
            || Distance(_maskPoints[^1], world) >= Math.Max(1d, _brushSize / 8d))
        {
            _maskPoints.Add(world);
            AddLiveMaskPreview(point);
        }
    }

    private void AddLiveMaskPreview(Point point)
    {
        Shape mark = _brushShape == SurveyBrushShape.Circle ? new Ellipse() : new Rectangle();
        mark.Width = _brushSize;
        mark.Height = _brushSize;
        mark.Fill = ActiveTool == SurveyEditorTool.Brush
            ? new SolidColorBrush(Color.FromArgb(185, _paintPreviewColor.R, _paintPreviewColor.G, _paintPreviewColor.B))
            : new SolidColorBrush(Color.FromArgb(205, 5, 10, 16));
        mark.Stroke = ActiveTool == SurveyEditorTool.Brush
            ? new SolidColorBrush(Color.FromArgb(230, _paintPreviewColor.R, _paintPreviewColor.G, _paintPreviewColor.B))
            : new SolidColorBrush(Color.FromArgb(180, 255, 110, 80));
        mark.StrokeThickness = 1d;
        mark.IsHitTestVisible = false;
        Canvas.SetLeft(mark, point.X - (_brushSize / 2d));
        Canvas.SetTop(mark, point.Y - (_brushSize / 2d));
        Canvas.SetZIndex(mark, 999_999);
        _liveMaskPreview.Add(mark);
        _canvas.Children.Add(mark);
    }

    private void ClearLiveMaskPreview()
    {
        foreach (var mark in _liveMaskPreview)
            _canvas.Children.Remove(mark);
        _liveMaskPreview.Clear();
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
        Canvas.SetZIndex(_brushPreview, 1_000_000);
        _canvas.Children.Add(_brushPreview);
        UpdateBrushPreviewAppearance();
        UpdateBrushPreviewSize();
        UpdatePointerVisuals();
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

    private void UpdateBrushPreviewAppearance()
    {
        if (_brushPreview is null)
            return;
        var isBrush = ActiveTool == SurveyEditorTool.Brush;
        if (_brushPreview.Fill is SolidColorBrush fill)
        {
            fill.Color = isBrush
                ? Color.FromArgb(80, _paintPreviewColor.R, _paintPreviewColor.G, _paintPreviewColor.B)
                : Color.FromArgb(38, 255, 110, 80);
        }
        if (_brushPreview.Stroke is SolidColorBrush stroke)
        {
            stroke.Color = isBrush
                ? PreviewOutlineColor(_paintPreviewColor)
                : Color.FromArgb(255, 255, 110, 80);
        }
    }

    private void UpdatePointerVisuals()
    {
        var showBrushPreview = !_temporaryNavigationActive
            && _pointerInsideCanvas
            && ActiveTool is SurveyEditorTool.Eraser or SurveyEditorTool.Brush;
        _brushPreview?.SetValue(
            VisibilityProperty,
            showBrushPreview ? Visibility.Visible : Visibility.Collapsed);
        ProtectedCursor = _temporaryNavigationActive
            ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
            : showBrushPreview
                ? null
                : ActiveTool switch
                {
                    SurveyEditorTool.Pan => Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Hand),
                    SurveyEditorTool.Eraser => Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Cross),
                    SurveyEditorTool.Template when _templateColorSamplerArmed
                        => Microsoft.UI.Input.InputSystemCursor.Create(
                            Microsoft.UI.Input.InputSystemCursorShape.Cross),
                    SurveyEditorTool.Brush or SurveyEditorTool.PaintBucket or SurveyEditorTool.Eyedropper
                        => Microsoft.UI.Input.InputSystemCursor.Create(
                            Microsoft.UI.Input.InputSystemCursorShape.Arrow),
                    _ => Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Arrow)
                };
    }

    private bool IsInsideCanvas(Point point) =>
        point.X >= 0d && point.Y >= 0d && point.X <= _canvas.Width && point.Y <= _canvas.Height;

    private static Color PreviewOutlineColor(SurveyColor color)
    {
        var luminance = ((0.299d * color.R) + (0.587d * color.G) + (0.114d * color.B)) / 255d;
        return luminance > 0.58d
            ? Color.FromArgb(255, 5, 10, 16)
            : Color.FromArgb(255, 255, 255, 255);
    }

    private void RaiseZoomChanged() => ZoomChanged?.Invoke(
        this,
        new SurveyZoomChangedEventArgs { Percent = ZoomPercent });

    private void PreserveWorldViewportPosition(double originDeltaX, double originDeltaY)
    {
        _viewportTransform.TranslateX -= originDeltaX * _viewportTransform.ScaleX;
        _viewportTransform.TranslateY -= originDeltaY * _viewportTransform.ScaleY;
        UpdateTransformBox();
    }

    private static double Distance(SurveyWorldPoint left, SurveyWorldPoint right)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
