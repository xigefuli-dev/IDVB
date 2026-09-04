using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MainSettingsPage
{
    private FrameworkElement CreateExperimentalAlgorithmsCard()
    {
        var menu = new MenuFlyout();
        var runtime = App.Session;
        var button = new DropDownButton
        {
            Content = "实验算法",
            Flyout = menu,
            MinWidth = 160
        };
        foreach (var option in ExperimentalAlgorithmRegistry.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = option.DisplayName,
                IsChecked = option.IsEnabled(runtime.Settings)
            };
            item.Click += async (_, _) =>
            {
                var previous = option.IsEnabled(runtime.Settings);
                item.IsEnabled = false;
                try
                {
                    await option.SetEnabledAsync(runtime, item.IsChecked);
                }
                catch (Exception exception)
                {
                    item.IsChecked = previous;
                    if (XamlRoot is not null)
                        await new ContentDialog
                        {
                            XamlRoot = XamlRoot,
                            Title = "设置未保存",
                            Content = exception.Message,
                            CloseButtonText = "知道了"
                        }.ShowAsync();
                }
                finally
                {
                    item.IsEnabled = true;
                }
            };
            menu.Items.Add(item);
        }

        return CreateControlCard(
            "实验算法",
            "实验线路默认关闭；开启后严格使用对应线路，不回退到常规线路",
            button);
    }

    private static Border CreateControlCard(
        string title,
        string description,
        FrameworkElement control)
    {
        var layout = new Grid
        {
            MinHeight = 86,
            Padding = new Thickness(26, 15, 24, 15)
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        layout.Children.Add(control);
        return new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }
}
