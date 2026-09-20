using IDVBuff.PluginContracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Features.Plugins;

public sealed partial class TeachingTipManager
{
    private static void RefreshPluginBindingButtonAppearance(
        Button button,
        PluginInputBinding binding,
        bool recording,
        bool hovered)
    {
        var isConfigured = binding.IsConfigured;
        var showReset = !recording && isConfigured && hovered;
        var background = recording
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 62, 115))
            : !isConfigured
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 132, 225))
            : showReset
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 55, 55))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 242, 242));
        var foreground = recording || !isConfigured || showReset
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
        var border = recording
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 14, 43, 82))
            : !isConfigured
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 105, 180))
            : showReset
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 35, 35))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 218, 218));

        button.Content = recording
            ? "请按按键…"
            : showReset ? "重置按键" : "设置按键";
        button.Background = background;
        button.Foreground = foreground;
        button.BorderBrush = border;

        // Keep WinUI's default Button template from replacing the state colors.
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;

        button.ApplyTemplate();
        button.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => ApplyButtonContentPresenterColors(
                button, background, border, foreground));
    }

    private static void ApplyButtonContentPresenterColors(
        Button button,
        SolidColorBrush background,
        SolidColorBrush border,
        SolidColorBrush foreground)
    {
        if (FindContentPresenter(button) is not { } presenter)
            return;

        presenter.Background = background;
        presenter.BorderBrush = border;
        presenter.Foreground = foreground;
    }

    private static ContentPresenter? FindContentPresenter(DependencyObject root)
    {
        if (root is ContentPresenter presenter)
            return presenter;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindContentPresenter(VisualTreeHelper.GetChild(root, index)) is { } child)
                return child;
        }

        return null;
    }
}
