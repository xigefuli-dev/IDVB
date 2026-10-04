using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace IDVBuff.Views;

/// <summary>Library of guided tutorials.</summary>
public sealed class HelpPage : Page
{
    private const string OnboardingVideoBaseUri = "https://download.xgflee.com/guides/onboarding/";

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
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
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

        var calibrationVideoButton = CreateVideoButton("vid1.mp4");
        var startMatchVideoButton = CreateVideoButton("vid2.mp4");
        var inGameVideoButton = CreateVideoButton("vid3.mp4");
        var endMatchVideoButton = CreateVideoButton("vid4.mp4");

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
                    Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush")
                },
                new TextBlock
                {
                    Text = App.IsSafeMode
                        ? "了解如何导入或选择地图，并以普通窗口形式展示。关闭安全模式后可使用完整的新手教程。"
                        : "从按键绑定开始，依次完成游戏地图、外置控件层、快捷扫描、楼层切换和地图缓存的配置。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
                },
                startTutorialButton
            }
        };
        Grid.SetColumn(tutorialContent, 1);

        var tutorialCard = new Border
        {
            Background = FluentTheme.Brush(this, "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"),
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
                        Background = FluentTheme.Brush(this, "AccentFillColorSecondaryBrush"),
                        Child = new SymbolIcon
                        {
                            Symbol = Symbol.Play,
                            Foreground = FluentTheme.Brush(this, "TextOnAccentFillColorPrimaryBrush")
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
                    Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush")
                },
                new TextBlock
                {
                    Text = "按自己的节奏学习 Identity Vision Bridge。每个教程都可以随时重新开始。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
                },
                new TextBlock
                {
                    Text = "开始学习",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush")
                },
                tutorialCard,
                new TextBlock
                {
                    Text = "更多教程",
                    Margin = new Thickness(0, 16, 0, 0),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush")
                },
                CreateCompactTutorialCard(subscribeTutorialButton),
                new TextBlock
                {
                    Text = "视频教程",
                    Margin = new Thickness(0, 16, 0, 0),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush")
                },
                CreateCompactTutorialCard(calibrationVideoButton, "校准显示区域", "框选完整的游戏地图画布，让 IDVB 正确识别地图区域。", Symbol.Video),
                CreateCompactTutorialCard(startMatchVideoButton, "如何开始对局", "打开外置控件层、选择本局地图并开始对局。", Symbol.Video),
                CreateCompactTutorialCard(inGameVideoButton, "游戏实机操作", "在游戏中打开地图、快捷扫描并选择对应地图。", Symbol.Video),
                CreateCompactTutorialCard(endMatchVideoButton, "结束游戏", "通过外置控件层结束本局。", Symbol.Video),
                new Border
                {
                    Padding = new Thickness(24, 20, 24, 20),
                    CornerRadius = new CornerRadius(10),
                    Background = FluentTheme.Brush(this, "ControlFillColorSecondaryBrush"),
                    Child = new TextBlock
                    {
                        Text = "更多地图操作和功能教程将在这里陆续加入。",
                        Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
                    }
                }
            }
        };
    }

    private static Button CreateVideoButton(string fileName)
    {
        var button = new Button
        {
            Content = "观看视频",
            MinWidth = 104,
            MinHeight = 34,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += async (_, _) =>
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri(OnboardingVideoBaseUri + fileName));
            }
            catch
            {
                // Keep the tutorial page available when the shell cannot open the video URL.
            }
        };
        return button;
    }

    private Border CreateCompactTutorialCard(Button button) =>
        CreateCompactTutorialCard(button, "订阅地图", "从地图社区选择地图包，并在 IDVB 中添加订阅。", Symbol.Download);

    private Border CreateCompactTutorialCard(Button button, string title, string description, Symbol icon)
    {
        var text = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
                }
            }
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(button, 2);
        return new Border
        {
            Background = FluentTheme.Brush(this, "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"),
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
                        Background = FluentTheme.Brush(this, "AccentFillColorSecondaryBrush"),
                        Child = new SymbolIcon { Symbol = icon, Foreground = FluentTheme.Brush(this, "TextOnAccentFillColorPrimaryBrush") }
                    },
                    text,
                    button
                }
            }
        };
    }
}
