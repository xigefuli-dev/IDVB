using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
    private FrameworkElement CreateGameShelf()
    {
        var games = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                CreateIdentityVGameTile(),
                CreateAddGamePlaceholder()
            }
        };

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "选择游戏",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = SecondaryTextBrush
                },
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Auto,
                    VerticalScrollMode = ScrollMode.Disabled,
                    Content = games
                }
            }
        };
    }

    private FrameworkElement CreateIdentityVGameTile()
    {
        var image = new Image
        {
            Width = 112,
            Height = 112,
            Stretch = Stretch.UniformToFill,
            Source = new BitmapImage(new Uri(
                "ms-appx:///Assets/Games/identity-v.png"))
        };
        var frame = new Border
        {
            Width = 120,
            Height = 120,
            Padding = new Thickness(3),
            Background = FluentTheme.Brush(this, "ControlFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush(this, "AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(20),
            Child = new Border
            {
                CornerRadius = new CornerRadius(16),
                Child = image
            }
        };
        var scale = new ScaleTransform { ScaleX = 0.9, ScaleY = 0.9 };
        var button = new Button
        {
            Width = 132,
            Height = 132,
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(22),
            Content = frame,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = scale,
            Shadow = new ThemeShadow()
        };
        AutomationProperties.SetName(button, "第五人格，已选择");
        ToolTipService.SetToolTip(button, "第五人格（已选择）");
        button.Loaded += (_, _) => AnimateScale(scale, 1, 180);
        button.PointerEntered += (_, _) => AnimateScale(scale, 1.035, 120);
        button.PointerExited += (_, _) => AnimateScale(scale, 1, 140);

        return new StackPanel
        {
            Width = 132,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                button,
                new TextBlock
                {
                    Text = "第五人格",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
    }

    private FrameworkElement CreateAddGamePlaceholder()
    {
        var plus = new Grid { Width = 32, Height = 32 };
        plus.Children.Add(new Border
        {
            Width = 30,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = SecondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        plus.Children.Add(new Border
        {
            Width = 2,
            Height = 30,
            CornerRadius = new CornerRadius(1),
            Background = SecondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var frame = new Border
        {
            Width = 88,
            Height = 88,
            Margin = new Thickness(0, 16, 0, 0),
            Background = FluentTheme.Brush(this, "ControlFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush(this, "ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = plus
        };
        AutomationProperties.SetName(frame, "添加游戏占位，更多游戏敬请期待");
        ToolTipService.SetToolTip(frame, "更多游戏，敬请期待");

        return new StackPanel
        {
            Width = 104,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                frame,
                new TextBlock
                {
                    Text = "更多游戏",
                    FontSize = 12,
                    Foreground = SecondaryTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
    }

    private static void AnimateScale(ScaleTransform scale, double target, int durationMilliseconds)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds));
        var scaleX = new DoubleAnimation
        {
            To = target,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        var scaleY = new DoubleAnimation
        {
            To = target,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(scaleX, scale);
        Storyboard.SetTargetProperty(scaleX, nameof(ScaleTransform.ScaleX));
        Storyboard.SetTarget(scaleY, scale);
        Storyboard.SetTargetProperty(scaleY, nameof(ScaleTransform.ScaleY));

        var storyboard = new Storyboard();
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Begin();
    }
}
