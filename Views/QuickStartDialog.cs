using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public enum QuickStartChoice
{
    Cancel,
    UseRecommendedSettings
}

/// <summary>
/// First-run quick-start dialog. The two action buttons are authored here so
/// the cancel action stays gray on the left and the recommended action stays
/// blue on the right across WinUI theme/template changes.
/// </summary>
public static class QuickStartDialog
{
    public static async Task<QuickStartChoice?> ShowAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
            return null;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "快速开始"
        };
        dialog.Resources["ContentDialogMaxWidth"] = 460d;

        var cancelButton = CreateActionButton(
            "取消",
            new Color { A = 255, R = 242, G = 242, B = 242 },
            new Color { A = 255, R = 218, G = 218, B = 218 },
            new Color { A = 255, R = 32, G = 32, B = 32 });
        var recommendedButton = CreateActionButton(
            "使用推荐设置",
            new Color { A = 255, R = 46, G = 132, B = 225 },
            new Color { A = 255, R = 30, G = 105, B = 180 },
            new Color { A = 255, R = 255, G = 255, B = 255 });

        var choice = QuickStartChoice.Cancel;
        cancelButton.Click += (_, _) =>
        {
            choice = QuickStartChoice.Cancel;
            dialog.Hide();
        };
        recommendedButton.Click += (_, _) =>
        {
            choice = QuickStartChoice.UseRecommendedSettings;
            dialog.Hide();
        };

        var actions = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 12
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(cancelButton, 0);
        Grid.SetColumn(recommendedButton, 1);
        actions.Children.Add(cancelButton);
        actions.Children.Add(recommendedButton);

        dialog.Content = new StackPanel
        {
            Spacing = 24,
            MinWidth = 360,
            Children =
            {
                new TextBlock
                {
                    Text = "嗨，如果你是第一次用这个软件，我建议你使用推荐的设置，这样会更方便使用！",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15
                },
                actions
            }
        };

        await dialog.ShowAsync();
        return choice;
    }

    private static Button CreateActionButton(
        string text,
        Color backgroundColor,
        Color borderColor,
        Color foregroundColor)
    {
        var background = new SolidColorBrush(backgroundColor);
        var border = new SolidColorBrush(borderColor);
        var foreground = new SolidColorBrush(foregroundColor);
        var button = new Button
        {
            Content = text,
            MinHeight = 40,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = background,
            BorderBrush = border,
            Foreground = foreground
        };

        // Keep the colors through the default Button template's visual states.
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        return button;
    }
}
