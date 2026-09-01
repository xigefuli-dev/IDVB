using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

/// <summary>Library of guided tutorials.</summary>
public sealed class HelpPage : Page
{
    public event EventHandler? ActivateGuideRequested;
    public event EventHandler? SubscribeMapsGuideRequested;

    public HelpPage()
    {
        var startTutorialButton = new Button
        {
            Content = App.IsSafeMode ? "查看教程" : "开始教程",
            MinWidth = 132,
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = FluentTheme.Brush("AccentFillColorDefaultBrush"),
            Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush"),
            CornerRadius = new CornerRadius(7)
        };
        startTutorialButton.Click += (_, _) => ActivateGuideRequested?.Invoke(this, EventArgs.Empty);

        var subscribeTutorialButton = new Button
        {
            Content = "开始教程",
            MinWidth = 104,
            MinHeight = 34,
            VerticalAlignment = VerticalAlignment.Center
        };
        subscribeTutorialButton.Click += (_, _) => SubscribeMapsGuideRequested?.Invoke(this, EventArgs.Empty);

        var tutorialContent = new StackPanel
        {
            Margin = new Thickness(18, 0, 0, 0),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "新手教程",
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
                },
                new TextBlock
                {
                    Text = App.IsSafeMode
                        ? "了解如何导入或选择地图，并以普通窗口形式展示。关闭安全模式后可使用完整的新手教程。"
                        : "从按键绑定开始，依次完成游戏地图、外置控件层、快捷扫描、楼层切换和地图缓存的配置。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                },
                startTutorialButton
            }
        };
        Grid.SetColumn(tutorialContent, 1);

        var tutorialCard = new Border
        {
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children =
                {
                    new Border
                    {
                        Width = 48,
                        Height = 48,
                        CornerRadius = new CornerRadius(24),
                        Background = FluentTheme.Brush("AccentFillColorSecondaryBrush"),
                        Child = new SymbolIcon
                        {
                            Symbol = Symbol.Play,
                            Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
                        }
                    },
                    tutorialContent
                }
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(48, 42, 48, 72),
            Spacing = 20,
            MaxWidth = 920,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new TextBlock
                {
                    Text = "教程",
                    FontSize = 32,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
                },
                new TextBlock
                {
                    Text = "按自己的节奏学习 Identity Vision Bridge。每个教程都可以随时重新开始。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                },
                new TextBlock
                {
                    Text = "开始学习",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
                },
                tutorialCard,
                new TextBlock
                {
                    Text = "更多教程",
                    Margin = new Thickness(0, 16, 0, 0),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
                },
                CreateCompactTutorialCard(subscribeTutorialButton),
                new Border
                {
                    Padding = new Thickness(24, 20, 24, 20),
                    CornerRadius = new CornerRadius(10),
                    Background = FluentTheme.Brush("ControlFillColorSecondaryBrush"),
                    Child = new TextBlock
                    {
                        Text = "更多地图操作和功能教程将在这里陆续加入。",
                        Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                    }
                }
            }
        };
    }

    private static Border CreateCompactTutorialCard(Button button)
    {
        var text = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "订阅地图", FontSize = 16, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "从地图社区选择地图包，并在 IDVB 中添加订阅。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                }
            }
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(button, 2);
        return new Border
        {
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new Grid
            {
                ColumnSpacing = 14,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children =
                {
                    new Border
                    {
                        Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = FluentTheme.Brush("AccentFillColorSecondaryBrush"),
                        Child = new SymbolIcon { Symbol = Symbol.Download, Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush") }
                    },
                    text,
                    button
                }
            }
        };
    }
}
