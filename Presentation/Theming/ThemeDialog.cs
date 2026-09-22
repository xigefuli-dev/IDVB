using IDVBuff.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Presentation.Theming;

internal static class ThemeDialog
{
    public static async Task<ContentDialogResult> ShowThemedAsync(this ContentDialog dialog, FrameworkElement? owner = null)
    {
        owner ??= dialog.XamlRoot?.Content as FrameworkElement
            ?? throw new InvalidOperationException("主题对话框需要所属窗口的 XamlRoot。");
        using var scope = ThemeService.AttachRegion(dialog, ThemeService.ProfileFor(owner));
        // ContentDialog initializes its background before entering the popup tree.
        // Bind its container explicitly; its native children use the local dictionary.
        dialog.Background = scope.Resources[ThemeToken.Dialog];
        dialog.Foreground = scope.Resources[ThemeToken.Text];
        dialog.BorderBrush = scope.Resources[ThemeToken.SurfaceBorder];
        return await dialog.ShowAsync();
    }
}
