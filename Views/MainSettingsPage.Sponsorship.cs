using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace IDVBuff.Views;

public sealed partial class MainSettingsPage
{
    internal const string SponsorshipUrl = SponsorshipAction.Url;

    private Border CreateSponsorCard()
    {
        var layout = new Grid { MinHeight = 86, Padding = new Thickness(26, 15, 24, 15) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = "赞助支持",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock
        {
            Text = "完全自愿；点击后在默认浏览器打开爱发电。",
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);

        var button = new Button
        {
            Content = "前往爱发电",
            MinWidth = 110,
            VerticalAlignment = VerticalAlignment.Center
        };
        var action = new SponsorshipAction(
            async uri => await Launcher.LaunchUriAsync(uri),
            enabled => button.IsEnabled = enabled,
            ShowSponsorLinkErrorAsync);
        button.Click += async (_, _) => await action.ClickAsync();
        Grid.SetColumn(button, 1);
        layout.Children.Add(button);

        return new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }

    private async Task ShowSponsorLinkErrorAsync()
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "链接未打开",
            Content = "无法打开浏览器，请稍后重试。",
            CloseButtonText = "知道了"
        }.ShowAsync();
    }
}
