using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private void ShowModernConcealProperties(Button placementTarget)
    {
        var defaults = _editorPreferenceState.ConcealDefaults;
        var panel = new StackPanel { Spacing = 10, Width = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = "遮瑕笔头",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(EditorText)
        });
        var shape = new ComboBox
        {
            Header = "形状",
            ItemsSource = new[] { "圆形", "正方形" },
            SelectedIndex = defaults.Shape == MapBackgroundLayerShape.Square ? 1 : 0
        };
        shape.SelectionChanged += async (_, _) =>
        {
            defaults.Shape = shape.SelectedIndex == 1
                ? MapBackgroundLayerShape.Square
                : MapBackgroundLayerShape.Circle;
            await SaveEditorPreferencesAsync();
            RenderModernEditor();
        };
        panel.Children.Add(shape);

        var value = new NumberBox
        {
            Header = "大小（像素）",
            Minimum = MapBackgroundProcessor.MinBrushSizePixels,
            Maximum = MapBackgroundProcessor.MaxBrushSizePixels,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = defaults.BrushSizePixels,
            SmallChange = 1,
            LargeChange = 16
        };
        var slider = new Slider
        {
            Minimum = MapBackgroundProcessor.MinBrushSizePixels,
            Maximum = MapBackgroundProcessor.MaxBrushSizePixels,
            StepFrequency = 1,
            Value = defaults.BrushSizePixels
        };
        var syncing = false;
        void ApplyValue(double next)
        {
            if (syncing || double.IsNaN(next))
                return;
            var rounded = MapBackgroundProcessor.ClampBrushSize((int)Math.Round(next));
            syncing = true;
            defaults.BrushSizePixels = rounded;
            slider.Value = rounded;
            value.Value = rounded;
            syncing = false;
            _ = SaveEditorPreferencesAsync();
            RenderModernEditor();
        }
        slider.ValueChanged += (_, args) => ApplyValue(args.NewValue);
        value.ValueChanged += (_, args) => ApplyValue(args.NewValue);
        panel.Children.Add(slider);
        panel.Children.Add(value);
        new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop
        }.ShowAt(placementTarget);
    }

    private void ApplyModernConcealBrushWheel(int delta)
    {
        if (_modernToolState.ActiveTool != MapEditorTool.Conceal || delta == 0)
            return;
        var defaults = _editorPreferenceState.ConcealDefaults;
        var multiplier = delta > 0 ? 1.1 : 1d / 1.1d;
        defaults.BrushSizePixels = MapBackgroundProcessor.ClampBrushSize(
            (int)Math.Round(defaults.BrushSizePixels * multiplier));
        _ = SaveEditorPreferencesAsync();
        RenderModernEditor();
    }

    private void UpdateModernConcealHover(Point point)
    {
        _modernConcealHoverPoint = _modernToolState.ActiveTool == MapEditorTool.Conceal
            ? ToModernNormalizedPoint(point, true)
            : null;
    }

    private void AddModernConcealLayer(MapBackgroundLayer layer, byte alpha = 92)
    {
        if (_modernCanvas is null || !layer.IsValid)
            return;
        var color = Color.FromArgb(alpha, 245, 86, 44);
        var points = layer.Points;
        var size = layer.BrushSizePixels;
        if (points.Count > 1)
        {
            var stroke = new Polyline
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = size,
                StrokeStartLineCap = layer.Shape == MapBackgroundLayerShape.Square
                    ? PenLineCap.Square : PenLineCap.Round,
                StrokeEndLineCap = layer.Shape == MapBackgroundLayerShape.Square
                    ? PenLineCap.Square : PenLineCap.Round,
                StrokeLineJoin = layer.Shape == MapBackgroundLayerShape.Square
                    ? PenLineJoin.Miter : PenLineJoin.Round,
                IsHitTestVisible = false
            };
            foreach (var point in points)
                stroke.Points.Add(new Point(point.X * _modernCanvas.Width, point.Y * _modernCanvas.Height));
            _modernCanvas.Children.Add(stroke);
        }

        // A single dot has no polyline segment. Square tips also need explicit end caps.
        var markers = layer.Shape == MapBackgroundLayerShape.Square && points.Count > 1
            ? new[] { points[0], points[^1] }
            : new[] { points[0] };
        foreach (var point in markers)
        {
            Shape marker = layer.Shape == MapBackgroundLayerShape.Square
                ? new Rectangle { Width = size, Height = size }
                : new Ellipse { Width = size, Height = size };
            marker.Fill = new SolidColorBrush(color);
            marker.IsHitTestVisible = false;
            Canvas.SetLeft(marker, point.X * _modernCanvas.Width - size / 2d);
            Canvas.SetTop(marker, point.Y * _modernCanvas.Height - size / 2d);
            _modernCanvas.Children.Add(marker);
        }
    }

    private void AddModernConcealPreview()
    {
        if (_modernCanvas is null)
            return;
        _modernConcealPreviewStroke = null;
        _modernConcealPreviewTip = null;
        _modernConcealPreviewPointCount = 0;
        var points = _modernConcealStroke.Points;
        if (points.Count > 0)
        {
            _modernConcealPreviewStroke = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(112, 245, 86, 44)),
                StrokeThickness = _modernConcealStroke.BrushSizePixels,
                StrokeStartLineCap = _modernConcealStroke.Shape == MapBackgroundLayerShape.Square
                    ? PenLineCap.Square : PenLineCap.Round,
                StrokeEndLineCap = _modernConcealStroke.Shape == MapBackgroundLayerShape.Square
                    ? PenLineCap.Square : PenLineCap.Round,
                StrokeLineJoin = _modernConcealStroke.Shape == MapBackgroundLayerShape.Square
                    ? PenLineJoin.Miter : PenLineJoin.Round,
                IsHitTestVisible = false
            };
            _modernCanvas.Children.Add(_modernConcealPreviewStroke);
            AppendModernConcealPreview();
        }
        if (_modernConcealHoverPoint is not { } hover)
            return;
        var defaults = _editorPreferenceState.ConcealDefaults;
        var x = hover.X * _modernCanvas.Width;
        var y = hover.Y * _modernCanvas.Height;
        var size = defaults.BrushSizePixels;
        _modernConcealPreviewTip = defaults.Shape == MapBackgroundLayerShape.Square
            ? new Rectangle { Width = size, Height = size }
            : new Ellipse { Width = size, Height = size };
        _modernConcealPreviewTip.Fill = new SolidColorBrush(Color.FromArgb(68, 245, 86, 44));
        _modernConcealPreviewTip.Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 130, 90));
        _modernConcealPreviewTip.StrokeThickness = Math.Max(1, 1 / ModernZoomFactor);
        _modernConcealPreviewTip.IsHitTestVisible = false;
        Canvas.SetLeft(_modernConcealPreviewTip, x - size / 2d);
        Canvas.SetTop(_modernConcealPreviewTip, y - size / 2d);
        _modernCanvas.Children.Add(_modernConcealPreviewTip);
    }

    private void AppendModernConcealPreview()
    {
        if (_modernCanvas is null || _modernConcealPreviewStroke is null)
            return;
        var points = _modernConcealStroke.Points;
        for (var index = _modernConcealPreviewPointCount; index < points.Count; index++)
            _modernConcealPreviewStroke.Points.Add(new Point(
                points[index].X * _modernCanvas.Width,
                points[index].Y * _modernCanvas.Height));
        _modernConcealPreviewPointCount = points.Count;
        if (_modernConcealPreviewTip is null || _modernConcealHoverPoint is not { } hover)
            return;
        Canvas.SetLeft(_modernConcealPreviewTip, hover.X * _modernCanvas.Width - _modernConcealPreviewTip.Width / 2d);
        Canvas.SetTop(_modernConcealPreviewTip, hover.Y * _modernCanvas.Height - _modernConcealPreviewTip.Height / 2d);
    }
}
