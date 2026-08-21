using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor
{
    private void ShowPaintProperties(Button anchor)
    {
        _paintFlyout ??= CreatePaintPropertiesFlyout();
        _paintFlyout.ShowAt(anchor);
    }

    private Flyout CreatePaintPropertiesFlyout()
    {
        var root = new StackPanel { Spacing = 8, Width = 270, Padding = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = "颜色与工具属性", FontSize = 15 });
        _paintColorPreview = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(255, _paintColor.R, _paintColor.G, _paintColor.B))
        };
        root.Children.Add(_paintColorPreview);
        _paintPicker = new ColorPicker
        {
            Color = Color.FromArgb(255, _paintColor.R, _paintColor.G, _paintColor.B),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false
        };
        _paintPicker.ColorChanged += (_, e) => SetPaintColor(new SurveyColor(e.NewColor.R, e.NewColor.G, e.NewColor.B));
        root.Children.Add(_paintPicker);
        _paintHexBox = new TextBox { Text = _paintColor.ToHex(), PlaceholderText = "#RRGGBB" };
        _paintHexBox.LostFocus += (_, _) =>
        {
            if (SurveyColor.TryParseHex(_paintHexBox.Text, out var color)) SetPaintColor(color);
            else
            {
                _paintHexBox.Text = _paintColor.ToHex();
                SetStatus("颜色代码必须是 #RRGGBB。", true);
            }
        };
        root.Children.Add(_paintHexBox);
        var sizeLabel = new TextBlock
        {
            Text = $"{(int)Math.Round(_brushSize)} px",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var size = new Slider
        {
            Header = "笔刷尺寸",
            Value = _brushSize,
            Minimum = 1,
            Maximum = 1024,
            StepFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        size.ValueChanged += (_, e) =>
        {
            if (double.IsFinite(e.NewValue))
            {
                _brushSize = Math.Clamp(Math.Round(e.NewValue), 1d, 1024d);
                sizeLabel.Text = $"{(int)_brushSize} px";
                _canvas.SetBrush(_brushSize, _brushShape);
            }
        };
        var sizeRow = new Grid { ColumnSpacing = 4 };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.Children.Add(size);
        Grid.SetColumn(sizeLabel, 1);
        sizeRow.Children.Add(sizeLabel);
        root.Children.Add(sizeRow);
        var shape = new ComboBox
        {
            Header = "笔头",
            ItemsSource = new[] { "圆形", "方形" },
            SelectedIndex = _brushShape == SurveyBrushShape.Circle ? 0 : 1
        };
        shape.SelectionChanged += (_, _) =>
        {
            _brushShape = shape.SelectedIndex == 1 ? SurveyBrushShape.Square : SurveyBrushShape.Circle;
            _canvas.SetBrush(_brushSize, _brushShape);
        };
        root.Children.Add(shape);
        var tolerance = new NumberBox { Header = "颜料桶容差", Value = _fillTolerance, Minimum = 0, Maximum = 255, SmallChange = 1 };
        tolerance.ValueChanged += (_, e) =>
        {
            if (double.IsFinite(e.NewValue)) _fillTolerance = (byte)Math.Clamp((int)e.NewValue, 0, 255);
        };
        root.Children.Add(tolerance);
        return new Flyout { Content = root };
    }
}
