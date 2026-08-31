using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;
public sealed partial class MapStatusPage : UserControl
{

    private UIElement CreateBindingRow(
        string title,
        string description,
        TextBlock value,
        MapRuntimeBindingTarget target)
    {
        var panel = new Grid { ColumnSpacing = 18 };
        panel.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(value);
        panel.Children.Add(text);
        var button = new Button
        {
            Content = "设置按键",
            MinWidth = 98,
            MinHeight = 38,
            Background = new SolidColorBrush(Color.FromArgb(255, 46, 132, 225)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 105, 180)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        button.Click += (_, _) => BindingButton_Click(target);
        button.PointerEntered += (_, _) =>
        {
            _bindingButtonHovered[target] = true;
            RefreshBindingButtonAppearance(target);
        };
        button.PointerExited += (_, _) =>
        {
            _bindingButtonHovered[target] = false;
            RefreshBindingButtonAppearance(target);
        };
        _bindingButtons[target] = button;
        _bindingRows[target] = panel;
        _bindingButtonHovered[target] = false;
        Grid.SetColumn(button, 1);
        panel.Children.Add(button);
        return panel;
    }

    private static UIElement CreateDiagnostic(string title, TextBlock value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        panel.Children.Add(value);
        return panel;
    }

    private static TextBlock CreateMutedText() => new()
    {
        FontSize = 13,
        Foreground = SecondaryTextBrush,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock CreateCategoryHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = PrimaryTextBrush,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static Button CreateActionButton(string text) => new()
    {
        Content = text,
        Background = new SolidColorBrush(Color.FromArgb(255, 46, 132, 225)),
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        MinWidth = 150,
        MinHeight = 45,
        HorizontalAlignment = HorizontalAlignment.Left,
        CornerRadius = new CornerRadius(8)
    };

    private static NumberBox CreatePercentageBox(
        string header,
        double minimum,
        double maximum) =>
        new()
        {
            Header = header,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 220
        };

    private static NumberBox CreateDecimalBox(
        string header,
        double minimum,
        double maximum,
        double step) =>
        new()
        {
            Header = header,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 260
        };

    private static Slider CreatePercentageSlider(string header) => new()
    {
        Header = header,
        Minimum = 0,
        Maximum = 100,
        StepFrequency = 1,
        TickFrequency = 10,
        IsThumbToolTipEnabled = true,
        MinWidth = 300,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private void BeginRecording(MapRuntimeBindingTarget target)
    {
        var previousTarget = _recording;
        _recording = target;
        _recordingHeldKeys.Clear();
        _recordingTriggerKey = 0;
        if (previousTarget is { } previous && previous != target)
            RefreshBindingButtonAppearance(previous);
        RefreshBindingButtonAppearance(target);
        _status.Text = target switch
        {
            MapRuntimeBindingTarget.GameMapToggle => "请按下游戏中用于打开/关闭地图的键盘或鼠标按键。",
            MapRuntimeBindingTarget.ControlPanelToggle => "请按下用于开启/关闭外置控件层的键盘或鼠标按键。",
            MapRuntimeBindingTarget.QuickScan => "请按下用于快捷扫描的键盘或鼠标按键。",
            MapRuntimeBindingTarget.OverlayToggle => "请按下用于切换识别图层的键盘或鼠标按键。",
            MapRuntimeBindingTarget.SwitchFloor => "请按下用于切换小地图楼层的键盘或鼠标按键。",
            MapRuntimeBindingTarget.SaveMapCache => "请按下用于保存当前地图缩放缓存的键盘或鼠标按键。",
            MapRuntimeBindingTarget.RestMapDisplay => "请按下用于保留地图身份并重置当前楼层对齐证据的键盘或鼠标按键。",
            _ => "请按下用于手动识别的键盘或鼠标按键。"
        };
        _root?.Focus(FocusState.Programmatic);
    }

}
