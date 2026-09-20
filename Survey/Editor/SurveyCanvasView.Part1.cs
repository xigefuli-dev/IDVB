using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;
internal sealed partial class SurveyCanvasView : Grid, IDisposable
{

    private void Canvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            BeginTemporaryNavigation();
            e.Handled = true;
            return;
        }
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

    private void Canvas_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space)
            return;
        EndTemporaryNavigation();
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
        KeyUp -= Canvas_KeyUp;
        _canvas.PointerExited -= Viewport_PointerExited;
        _canvas.PointerEntered -= Viewport_PointerEntered;
        LostFocus -= Viewport_LostFocus;
        EndTemporaryNavigation();
        foreach (var wrapper in _visuals.Values)
            ReleaseLayerVisual(wrapper);
        _visuals.Clear();
        _visualContentKeys.Clear();
        _selectedLayerIds.Clear();
        _primaryLayerId = null;
        _isolatedLayerId = null;
        _dragLayerId = null;
        _dragVisualTransform = null;
        DisposeTransformBox();
        ClearLiveMaskPreview();
        _brushPreview = null;
        _canvas.Children.Clear();
        Children.Clear();
        LayerSelected = null;
        TransformCommitted = null;
        ZoomChanged = null;
        LayerToolInvoked = null;
        LayerPixelSampleRequested = null;
        MaskStrokeCommitted = null;
    }

}
