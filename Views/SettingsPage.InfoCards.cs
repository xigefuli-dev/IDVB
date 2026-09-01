using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class SettingsPage
{
    private static Border CreateInfoCard(
        string label,
        string value,
        string detail,
        Symbol icon,
        string? githubLabel = null)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new SymbolIcon(icon) { Foreground = AccentBrush });
        content.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = SecondaryTextBrush });
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        if (githubLabel is not null)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12
            };
            actions.Children.Add(new HyperlinkButton
            {
                Content = githubLabel,
                NavigateUri = new Uri("https://github.com/xigefuli-dev/IDVB"),
                Padding = new Thickness(0, 2, 0, 2)
            });
            actions.Children.Add(new HyperlinkButton
            {
                Content = new TextBlock
                {
                    Text = "[赞助此项目]",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 108, 230)),
                    TextDecorations = Windows.UI.Text.TextDecorations.Underline
                },
                Padding = new Thickness(0, 2, 0, 2)
            });
            content.Children.Add(actions);
        }
        return new Border
        {
            Padding = new Thickness(18),
            MinHeight = 150,
            Background = FluentTheme.CardBrush(),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
    }
}
