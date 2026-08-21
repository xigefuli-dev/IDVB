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
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var x = point.X * _modernCanvas.Width;
            var y = point.Y * _modernCanvas.Height;
            if (index > 0)
            {
                var previous = points[index - 1];
                _modernCanvas.Children.Add(new Line
                {
                    X1 = previous.X * _modernCanvas.Width,
                    Y1 = previous.Y * _modernCanvas.Height,
                    X2 = x,
                    Y2 = y,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = layer.BrushSizePixels,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                });
            }
            var size = layer.BrushSizePixels;
            Shape marker = layer.Shape == MapBackgroundLayerShape.Square
                ? new Rectangle { Width = size, Height = size }
                : new Ellipse { Width = size, Height = size };
            marker.Fill = new SolidColorBrush(color);
            marker.IsHitTestVisible = false;
            Canvas.SetLeft(marker, x - size / 2d);
            Canvas.SetTop(marker, y - size / 2d);
            _modernCanvas.Children.Add(marker);
        }
    }

    private void AddModernConcealPreview()
    {
        if (_modernCanvas is null)
            return;
        var points = _modernConcealStroke.Points;
        if (points.Count > 0)
        {
            var layer = new MapBackgroundLayer
            {
                Shape = _modernConcealStroke.Shape,
                BrushSizePixels = _modernConcealStroke.BrushSizePixels,
                Points = points.Select(point => point.Clone()).ToList()
            };
            AddModernConcealLayer(layer, 112);
        }
        if (_modernConcealHoverPoint is not { } hover)
            return;
        var defaults = _editorPreferenceState.ConcealDefaults;
        var x = hover.X * _modernCanvas.Width;
        var y = hover.Y * _modernCanvas.Height;
        var size = defaults.BrushSizePixels;
        Shape marker = defaults.Shape == MapBackgroundLayerShape.Square
            ? new Rectangle { Width = size, Height = size }
            : new Ellipse { Width = size, Height = size };
        marker.Fill = new SolidColorBrush(Color.FromArgb(68, 245, 86, 44));
        marker.Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 130, 90));
        marker.StrokeThickness = Math.Max(1, 1 / ModernZoomFactor);
        marker.IsHitTestVisible = false;
        Canvas.SetLeft(marker, x - size / 2d);
        Canvas.SetTop(marker, y - size / 2d);
        _modernCanvas.Children.Add(marker);
    }
}
