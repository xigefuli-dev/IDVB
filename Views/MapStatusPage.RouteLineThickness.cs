using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private async void HideMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetHideMiniMapAsync(_hideMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }


    private readonly Slider _routeLineThicknessSlider = new()
    {
        Header = "线路粗细",
        Minimum = 0,
        Maximum = 3,
        StepFrequency = 1,
        TickFrequency = 1,
        TickPlacement = TickPlacement.Outside,
        SnapsTo = SliderSnapsTo.Ticks,
        IsThumbToolTipEnabled = false,
        Value = 1,
        Width = 300,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _routeLineThicknessValue = new() { Text = "当前：中" };

    private static string RouteLineThicknessLabel(int level) => level switch
    {
        0 => "细",
        1 => "中",
        2 => "粗",
        _ => "更粗"
    };

    private StackPanel CreateRouteLineThicknessPanel()
    {
        var labels = new Grid { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < 3; i++)
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
        {
            var label = new TextBlock
            {
                Text = RouteLineThicknessLabel(i),
                HorizontalAlignment = i == 3 ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            Grid.SetColumn(label, Math.Min(i, 2));
            labels.Children.Add(label);
        }
        return new StackPanel { Spacing = 4, Children =
        {
            _routeLineThicknessSlider, labels, _routeLineThicknessValue
        } };
    }

    private async void RouteLineThickness_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_refreshing) return;
        var level = Math.Clamp((int)Math.Round(args.NewValue), 0, 3);
        _routeLineThicknessValue.Text = $"当前：{RouteLineThicknessLabel(level)}";
        if (_runtime.Settings.RouteLineThickness == level) return;
        try { await _runtime.SetRouteLineThicknessAsync(level); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }
}
