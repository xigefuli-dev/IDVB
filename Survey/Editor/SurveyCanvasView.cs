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

internal sealed partial class SurveyCanvasView : Grid, IDisposable
{
    private const double CanvasPadding = 80d;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<Guid, Border> _visuals = [];
    private readonly Dictionary<Guid, string> _visualContentKeys = [];
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
    private CancellationTokenSource? _renderCancellation;
    private bool _disposed;

    public SurveyCanvasView()
    {
        IsTabStop = true;
        Background = new SolidColorBrush(Color.FromArgb(255, 5, 10, 16));
        _canvas.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        _canvas.HorizontalAlignment = HorizontalAlignment.Left;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_canvas);
        InitializeViewportInteractions();
        KeyDown += Canvas_KeyDown;
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
            var outline = GetSelectionOutline(pair.Value);
            outline.BorderBrush = new SolidColorBrush(
                pair.Key == _primaryLayerId
                    ? Color.FromArgb(255, 91, 176, 255)
                    : selected
                        ? Color.FromArgb(255, 78, 205, 196)
                    : Color.FromArgb(0, 0, 0, 0));
            outline.BorderThickness = selected
                ? new Thickness(2)
                : new Thickness(0);
        }
    }

    public async Task RenderAsync(
        SurveyEditorSession session,
        string floorKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var previousRender = _renderCancellation;
        _renderCancellation = renderCancellation;
        previousRender?.Cancel();
        var generation = ++_renderGeneration;
        var renderToken = renderCancellation.Token;
        try
        {
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
            var loadedBitmaps = new Dictionary<Guid, Microsoft.UI.Xaml.Media.Imaging.BitmapImage>();
            foreach (var layer in layers)
            {
                renderToken.ThrowIfCancellationRequested();
                if (generation != _renderGeneration
                    || !observations.TryGetValue(layer.ObservationId, out var observation))
                    return;
                var contentKey = LayerContentKey(layer, observation);
                if (_visuals.ContainsKey(layer.LayerId)
                    && _visualContentKeys.GetValueOrDefault(layer.LayerId) == contentKey)
                    continue;
                var bitmap = await SurveyBitmapLoader.LoadLayerAsync(
                    session,
                    layer.LayerId,
                    cancellationToken: renderToken);
                renderToken.ThrowIfCancellationRequested();
                if (generation != _renderGeneration)
                    return;
                loadedBitmaps[layer.LayerId] = bitmap;
            }

            renderToken.ThrowIfCancellationRequested();
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
            var activeIds = layers.Select(item => item.LayerId).ToHashSet();
            foreach (var removedId in _visuals.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            {
                var removed = _visuals[removedId];
                ReleaseLayerVisual(removed);
                _canvas.Children.Remove(removed);
                _visuals.Remove(removedId);
                _visualContentKeys.Remove(removedId);
            }
            for (var index = 0; index < layers.Length; index++)
            {
                var layer = layers[index];
                var observation = observations[layer.ObservationId];
                if (_visuals.TryGetValue(layer.LayerId, out var existing))
                    UpdateLayerVisual(existing, layer, observation,
                        loadedBitmaps.GetValueOrDefault(layer.LayerId), originX, originY);
                else
                    AddLayerVisual(layer, observation, loadedBitmaps[layer.LayerId], originX, originY);
                Canvas.SetZIndex(_visuals[layer.LayerId], index);
                _visualContentKeys[layer.LayerId] = LayerContentKey(layer, observation);
            }
            ClearLiveMaskPreview();
            var selectedIds = _selectedLayerIds.ToArray();
            SelectLayers(selectedIds, _primaryLayerId);
            RecreateBrushPreview();
            if (!_hasInitialFit)
            {
                _hasInitialFit = true;
                DispatcherQueue.TryEnqueue(FitToViewport);
            }
        }
        finally
        {
            if (ReferenceEquals(_renderCancellation, renderCancellation))
                _renderCancellation = null;
            renderCancellation.Dispose();
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
        var content = new Grid
        {
            Width = observation.SourceAsset.PixelWidth,
            Height = observation.SourceAsset.PixelHeight
        };
        content.Children.Add(image);
        content.Children.Add(new Border
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        });
        var wrapper = new Border
        {
            Width = observation.SourceAsset.PixelWidth,
            Height = observation.SourceAsset.PixelHeight,
            Child = content,
            Visibility = layer.IsVisible ? Visibility.Visible : Visibility.Collapsed,
            RenderTransformOrigin = new Point(0d, 0d),
            RenderTransform = composite,
            Tag = layer
        };
        // A Border without a background only hit-tests its narrow border.  Keep the
        // image transparent to input, but make its complete rectangular area active.
        wrapper.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        wrapper.PointerPressed += Layer_PointerPressed;
        wrapper.PointerMoved += Layer_PointerMoved;
        wrapper.PointerReleased += Layer_PointerReleased;
        wrapper.PointerCanceled += Layer_PointerCanceled;
        _visuals[layer.LayerId] = wrapper;
        _canvas.Children.Add(wrapper);
    }

    private static void UpdateLayerVisual(
        Border wrapper,
        SurveyMapLayer layer,
        SurveyObservation observation,
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage? replacementBitmap,
        double originX,
        double originY)
    {
        wrapper.Width = observation.SourceAsset.PixelWidth;
        wrapper.Height = observation.SourceAsset.PixelHeight;
        wrapper.Visibility = layer.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        wrapper.RenderTransform = CreateCompositeTransform(layer.EffectiveTransform, originX, originY);
        wrapper.Tag = layer;
        if (wrapper.Child is not Grid content || content.Children[0] is not Image image)
            return;
        content.Width = observation.SourceAsset.PixelWidth;
        content.Height = observation.SourceAsset.PixelHeight;
        image.Width = observation.SourceAsset.PixelWidth;
        image.Height = observation.SourceAsset.PixelHeight;
        image.Opacity = layer.Opacity;
        if (replacementBitmap is not null)
            image.Source = replacementBitmap;
    }

    private static Border GetSelectionOutline(Border wrapper) =>
        wrapper.Child is Grid content && content.Children.Count > 1 && content.Children[1] is Border outline
            ? outline
            : throw new InvalidOperationException("Survey layer selection outline is missing.");

    private void ReleaseLayerVisual(Border wrapper)
    {
        wrapper.PointerPressed -= Layer_PointerPressed;
        wrapper.PointerMoved -= Layer_PointerMoved;
        wrapper.PointerReleased -= Layer_PointerReleased;
        wrapper.PointerCanceled -= Layer_PointerCanceled;
        if (wrapper.Child is Grid content
            && content.Children.FirstOrDefault() is Image image)
        {
            image.Source = null;
        }
        wrapper.Child = null;
        wrapper.Tag = null;
    }

    private static string LayerContentKey(SurveyMapLayer layer, SurveyObservation observation)
    {
        var displayAsset = layer.ColorFilterAsset ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
            ? observation.DisplayAsset
            : observation.SourceAsset);
        return $"{displayAsset.Sha256}:{layer.HiddenMaskAsset?.Sha256}:{layer.Brightness:R}";
    }

    private void Layer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: SurveyMapLayer layer } wrapper)
            return;
        if (ActiveTool is SurveyEditorTool.Decontaminate or SurveyEditorTool.Align or SurveyEditorTool.NormalizeColors)
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
        Focus(FocusState.Pointer);
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

    private void Canvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ActiveTool != SurveyEditorTool.Select
            || _selectedLayerIds.Count != 1
            || _primaryLayerId is not { } layerId
            || !_visuals.TryGetValue(layerId, out var wrapper)
            || wrapper.Tag is not SurveyMapLayer { IsLocked: false } layer
            || wrapper.RenderTransform is not CompositeTransform visual)
            return;

        var (dx, dy) = e.Key switch
        {
            Windows.System.VirtualKey.Left => (-1d, 0d),
            Windows.System.VirtualKey.Right => (1d, 0d),
            Windows.System.VirtualKey.Up => (0d, -1d),
            Windows.System.VirtualKey.Down => (0d, 1d),
            _ => (0d, 0d)
        };
        if (dx == 0d && dy == 0d)
            return;

        visual.TranslateX += dx;
        visual.TranslateY += dy;
        TransformCommitted?.Invoke(this, new SurveyLayerTransformEventArgs
        {
            LayerId = layerId,
            Transform = layer.EffectiveTransform with
            {
                TranslationX = visual.TranslateX - _originX,
                TranslationY = visual.TranslateY - _originY
            }
        });
        e.Handled = true;
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ++_renderGeneration;
        _renderCancellation?.Cancel();
        _renderCancellation = null;
        KeyDown -= Canvas_KeyDown;
        foreach (var wrapper in _visuals.Values)
            ReleaseLayerVisual(wrapper);
        _visuals.Clear();
        _visualContentKeys.Clear();
        _selectedLayerIds.Clear();
        _primaryLayerId = null;
        _dragLayerId = null;
        _dragVisualTransform = null;
        ClearLiveMaskPreview();
        _brushPreview = null;
        _canvas.Children.Clear();
        Children.Clear();
        LayerSelected = null;
        TransformCommitted = null;
        ZoomChanged = null;
        LayerToolInvoked = null;
        MaskStrokeCommitted = null;
    }

}
