using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

/// <summary>Entry point for replaying the first-run configuration guide.</summary>
public sealed class HelpPage : Page
{
    public event EventHandler? ActivateGuideRequested;

    public HelpPage()
    {
        var activateButton = new Button
        {
            Content = "激活按键绑定引导",
            MinWidth = 220,
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = FluentTheme.Brush("AccentFillColorDefaultBrush"),
            Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush"),
            CornerRadius = new CornerRadius(7)
        };
        activateButton.Click += (_, _) => ActivateGuideRequested?.Invoke(this, EventArgs.Empty);

        Content = new StackPanel
        {
            Margin = new Thickness(40, 36, 40, 64),
            Spacing = 16,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new TextBlock
                {
                    Text = "帮助",
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
                },
                new TextBlock
                {
                    Text = "重新打开按键绑定引导，逐项完成游戏地图开关、外置控件层、快捷扫描、切换楼层和保存地图缓存的配置。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                },
                activateButton
            }
        };
    }
}
