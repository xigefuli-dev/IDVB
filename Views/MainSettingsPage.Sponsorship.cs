using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MainSettingsPage
{
    internal const string SponsorshipUrl = SponsorshipAction.Url;
    public event EventHandler? SponsorshipRequested;

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
            Text = "让探索继续。微信、支付宝与爱发电，支持方式由你选择。",
            FontSize = 14,
            Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);

        var button = new Button
        {
            Content = "赞助支持",
            MinWidth = 110,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => SponsorshipRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(button, 1);
        layout.Children.Add(button);

        return new Border
        {
            Background = FluentTheme.CardBrush(this),
            BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }
}
