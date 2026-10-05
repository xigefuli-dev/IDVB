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
/// accent-colored on the right across WinUI theme/template changes.
/// </summary>
public static class QuickStartDialog
{
    public static Task<QuickStartChoice?> ShowAsync(XamlRoot? xamlRoot) =>
        ShowAsync(xamlRoot, CancellationToken.None);

    public static async Task<QuickStartChoice?> ShowAsync(XamlRoot? xamlRoot, CancellationToken cancellationToken)
    {
        if (xamlRoot is null)
            return null;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "快速开始"
        };
        dialog.Resources["ContentDialogMaxWidth"] = 460d;

        var cancelButton = CreateActionButton("取消", IDVBuff.Appearance.ThemeButtonRole.Standard);
        var recommendedButton = CreateActionButton("使用推荐设置", IDVBuff.Appearance.ThemeButtonRole.Accent);

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

        await dialog.ShowThemedAsync(cancellationToken: cancellationToken);
        return choice;
    }

    private static Button CreateActionButton(string text, IDVBuff.Appearance.ThemeButtonRole role) =>
        ThemeButton.Apply(new Button
        {
            Content = text,
            MinHeight = 40,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        }, role);
}
