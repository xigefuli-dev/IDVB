using IDVBuff.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Presentation.Theming;

internal static class ThemeDialog
{
    public static async Task<ContentDialogResult> ShowThemedAsync(
        this ContentDialog dialog, FrameworkElement? owner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        owner ??= dialog.XamlRoot?.Content as FrameworkElement
            ?? throw new InvalidOperationException("主题对话框需要所属窗口的 XamlRoot。");
        // Explicitly resolve the native template before ShowAsync for WinUI #8476
        // (missing entrance animation). Keep caller-provided styles intact.
        if (dialog.Style is null)
            dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        using var scope = ThemeService.AttachRegion(dialog, ThemeService.ProfileFor(owner));
        // ContentDialog initializes its background before entering the popup tree.
        // Bind its container explicitly; its native children use the local dictionary.
        dialog.Background = scope.Resources[ThemeToken.Dialog];
        dialog.Foreground = scope.Resources[ThemeToken.Text];
        dialog.BorderBrush = scope.Resources[ThemeToken.SurfaceBorder];
        var dispatcher = dialog.DispatcherQueue;
        using var cancellation = cancellationToken.Register(() =>
            dispatcher.TryEnqueue(() => dialog.Hide()));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
