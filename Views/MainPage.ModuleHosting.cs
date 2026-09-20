using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private void ConfigureMainContentScrolling(object view)
    {
        var isMapListPage = view is MapListPage;
        var isPluginsPage = view is PluginsPage;

        // MapListPage owns the list/editor viewport. Disable the host viewport so
        // its almost-empty outer page scrollbar cannot appear beside the real one.
        MainContentHost.VerticalScrollMode = isMapListPage
            ? ScrollMode.Disabled
            : ScrollMode.Enabled;
        MainContentHost.VerticalScrollBarVisibility = isMapListPage
            ? ScrollBarVisibility.Disabled
            : isPluginsPage
                ? ScrollBarVisibility.Hidden
                : ScrollBarVisibility.Auto;
        MainContentHost.ChangeView(0, 0, null, true);
    }

    private static FrameworkElement CreateModuleFailureView(
        string moduleId,
        Exception exception) =>
        new StackPanel
        {
            Margin = new Thickness(48, 42, 48, 72),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "页面加载失败",
                    FontSize = 29,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = $"模块 {moduleId} 初始化失败，应用其余功能仍可继续使用。",
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Background = new SolidColorBrush(
                        Color.FromArgb(32, 255, 72, 72)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Child = new TextBlock
                    {
                        Text = $"{exception.GetType().Name}: {exception.Message}",
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

    private static void TryLogModuleFailure(
        string moduleId,
        Exception exception,
        string stage = "create-view")
    {
        try
        {
            App.Session.LogCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Error,
                $"Module '{moduleId}' failed to create its view: {exception.Message}",
                details: new()
                {
                    ["moduleId"] = moduleId,
                    ["stage"] = stage,
                    ["exceptionType"] = exception.GetType().FullName
                        ?? exception.GetType().Name
                });
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Module view failure could not be logged: {loggingException}");
        }
    }
}
