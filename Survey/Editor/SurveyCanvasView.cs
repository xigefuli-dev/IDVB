using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

internal sealed class SurveyLayerTransformEventArgs : EventArgs
{
    public required Guid LayerId { get; init; }
    public required SurveyLayerTransform Transform { get; init; }
}

internal sealed partial class SurveyCanvasView : Grid
{
    private const double CanvasPadding = 80d;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<Guid, Border> _visuals = [];
    private int _renderGeneration;
    private readonly HashSet<Guid> _selectedLayerIds = [];
    private Guid? _primaryLayerId;
    private Guid? _dragLayerId;
    private Point _dragStart;
    private SurveyLayerTransform _dragTransform;
    private CompositeTransform? _dragVisualTransform;
    private double _originX;
    private double _originY;
    private Guid? _renderedFloorId;

    public SurveyCanvasView()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 5, 10, 16));
        _canvas.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        _canvas.HorizontalAlignment = HorizontalAlignment.Left;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_canvas);
        InitializeViewportInteractions();
    }

    public event EventHandler<Guid>? LayerSelected;
    public event EventHandler<SurveyLayerTransformEventArgs>? TransformCommitted;

    public void SelectLayer(Guid? layerId)
    {
        SelectLayers(layerId is null ? [] : [layerId.Value], layerId);
    }

    public void SelectLayers(IEnumerable<Guid> layerIds, Guid? primaryLayerId)
    {
        _selectedLayerIds.Clear();
        foreach (var layerId in layerIds)
            _selectedLayerIds.Add(layerId);
        _primaryLayerId = primaryLayerId is { } primary && _selectedLayerIds.Contains(primary)
            ? primary
            : _selectedLayerIds.Count == 0 ? null : _selectedLayerIds.First();
        foreach (var pair in _visuals)
        {
            var selected = _selectedLayerIds.Contains(pair.Key);
            pair.Value.BorderBrush = new SolidColorBrush(
                pair.Key == _primaryLayerId
                    ? Color.FromArgb(255, 91, 176, 255)
                    : selected
                        ? Color.FromArgb(255, 78, 205, 196)
                    : Color.FromArgb(0, 0, 0, 0));
            pair.Value.BorderThickness = selected
                ? new Thickness(2)
                : new Thickness(0);
        }
    }

    public async Task RenderAsync(
        SurveyEditorSession session,
        string floorKey,
        CancellationToken cancellationToken = default)
    {
        var generation = ++_renderGeneration;
        _canvas.Children.Clear();
        _visuals.Clear();
        if (session.Snapshot is not { } snapshot)
            return;

        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase));
        if (floor is null)
            return;
        var observations = snapshot.Observations.ToDictionary(item => item.ObservationId);
        var layers = snapshot.Layers
            .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted)
            .OrderBy(item => item.ZOrder)
            .ToArray();
        var bounds = CalculateBounds(layers, observations);
        var originX = CanvasPadding - bounds.X;
        var originY = CanvasPadding - bounds.Y;
        if (_renderedFloorId == floor.FloorId)
        {
            PreserveWorldViewportPosition(
                originX - _originX,
                originY - _originY);
        }
        _originX = originX;
        _originY = originY;
        _renderedFloorId = floor.FloorId;
        _canvas.Width = Math.Max(800d, bounds.Width + (CanvasPadding * 2d));
        _canvas.Height = Math.Max(600d, bounds.Height + (CanvasPadding * 2d));

        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _renderGeneration
                || !observations.TryGetValue(layer.ObservationId, out var observation))
                return;
            var bitmap = await SurveyBitmapLoader.LoadLayerAsync(
                session,
                layer.LayerId,
                cancellationToken: cancellationToken);
            if (generation != _renderGeneration)
                return;
            AddLayerVisual(layer, observation, bitmap, originX, originY);
        }
        var selectedIds = _selectedLayerIds.ToArray();
        SelectLayers(selectedIds, _primaryLayerId);
        RecreateBrushPreview();
        if (!_hasInitialFit)
        {
            _hasInitialFit = true;
            DispatcherQueue.TryEnqueue(FitToViewport);
        }
    }

    private void AddLayerVisual(
        SurveyMapLayer layer,
        SurveyObservation observation,
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap,
        double originX,
        double originY)
    {
        var transform = layer.EffectiveTransform;
        var composite = CreateCompositeTransform(transform, originX, originY);
        var image = new Image
        {
            Source = bitmap,
            Width = observation.SourceAsset.PixelWidth,
            Height = observation.SourceAsset.PixelHeight,
            Stretch = Stretch.Fill,
            Opacity = layer.Opacity,
            IsHitTestVisible = false
        };
        var wrapper = new Border
        {
            Width = observation.SourceAsset.PixelWidth,
            Height = observation.SourceAsset.PixelHeight,
            Child = image,
            Visibility = layer.IsVisible ? Visibility.Visible : Visibility.Collapsed,
            RenderTransformOrigin = new Point(0d, 0d),
            RenderTransform = composite,
            Tag = layer
        };
        wrapper.PointerPressed += Layer_PointerPressed;
        wrapper.PointerMoved += Layer_PointerMoved;
        wrapper.PointerReleased += Layer_PointerReleased;
        wrapper.PointerCanceled += Layer_PointerCanceled;
        _visuals[layer.LayerId] = wrapper;
        _canvas.Children.Add(wrapper);
    }

    private void Layer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: SurveyMapLayer layer } wrapper)
            return;
        if (ActiveTool is SurveyEditorTool.Decontaminate or SurveyEditorTool.Align)
        {
            LayerToolInvoked?.Invoke(this, new SurveyLayerToolEventArgs
            {
                LayerId = layer.LayerId,
                Tool = ActiveTool
            });
            e.Handled = true;
            return;
        }
        if (ActiveTool != SurveyEditorTool.Select)
            return;
        SelectLayer(layer.LayerId);
        LayerSelected?.Invoke(this, layer.LayerId);
        if (layer.IsLocked)
            return;
        _dragLayerId = layer.LayerId;
        _dragStart = e.GetCurrentPoint(_canvas).Position;
        _dragTransform = layer.EffectiveTransform;
        _dragVisualTransform = (CompositeTransform)wrapper.RenderTransform;
        wrapper.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Layer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragLayerId is null || _dragVisualTransform is null)
            return;
        var point = e.GetCurrentPoint(_canvas).Position;
        _dragVisualTransform.TranslateX += point.X - _dragStart.X;
        _dragVisualTransform.TranslateY += point.Y - _dragStart.Y;
        _dragTransform = _dragTransform with
        {
            TranslationX = _dragTransform.TranslationX + point.X - _dragStart.X,
            TranslationY = _dragTransform.TranslationY + point.Y - _dragStart.Y
        };
        _dragStart = point;
        e.Handled = true;
    }

    private void Layer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
            element.ReleasePointerCapture(e.Pointer);
        CommitDrag();
        e.Handled = true;
    }

    private void Layer_PointerCanceled(object sender, PointerRoutedEventArgs e) => CommitDrag();

    private void CommitDrag()
    {
        if (_dragLayerId is { } layerId)
        {
            TransformCommitted?.Invoke(this, new SurveyLayerTransformEventArgs
            {
                LayerId = layerId,
                Transform = _dragTransform
            });
        }
        _dragLayerId = null;
        _dragVisualTransform = null;
    }

    private static CompositeTransform CreateCompositeTransform(
        SurveyLayerTransform transform,
        double originX,
        double originY) => new()
    {
        ScaleX = transform.ScaleX,
        ScaleY = transform.ScaleY,
        Rotation = transform.RotationDegrees,
        TranslateX = transform.TranslationX + originX,
        TranslateY = transform.TranslationY + originY,
        CenterX = 0d,
        CenterY = 0d
    };

    private static SurveyWorldRect CalculateBounds(
        IReadOnlyList<SurveyMapLayer> layers,
        IReadOnlyDictionary<Guid, SurveyObservation> observations)
    {
        if (layers.Count == 0)
            return new SurveyWorldRect(0d, 0d, 640d, 440d);
        var points = new List<SurveyWorldPoint>(layers.Count * 4);
        foreach (var layer in layers)
        {
            if (!observations.TryGetValue(layer.ObservationId, out var observation))
                continue;
            var width = observation.SourceAsset.PixelWidth;
            var height = observation.SourceAsset.PixelHeight;
            var transform = layer.EffectiveTransform;
            points.Add(transform.Transform(new SurveyWorldPoint(0d, 0d)));
            points.Add(transform.Transform(new SurveyWorldPoint(width, 0d)));
            points.Add(transform.Transform(new SurveyWorldPoint(0d, height)));
            points.Add(transform.Transform(new SurveyWorldPoint(width, height)));
        }
        if (points.Count == 0)
            return new SurveyWorldRect(0d, 0d, 640d, 440d);
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        return new SurveyWorldRect(minX, minY, maxX - minX, maxY - minY);
    }

}
