using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage : UserControl
{
    private static Brush PrimaryTextBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush SecondaryTextBrush => FluentTheme.Brush("TextFillColorSecondaryBrush");

    private FrameworkElement CreatePageFailureView(Exception exception) =>
        new StackPanel
        {
            Margin = new Thickness(36, 31, 36, 38),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "状态页部分功能加载失败",
                    FontSize = 29,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "错误已隔离，地图运行时和应用其他页面不会因此退出。",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = $"{exception.GetType().Name}: {exception.Message}",
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

    private void ReportPageFailure(string stage, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(
            $"Map status page {stage} failed: {exception}");
        try
        {
            _runtime.LogCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Error,
                $"Map status page {stage} failed: {exception.Message}",
                details: new()
                {
                    ["stage"] = stage,
                    ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name
                });
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Map status page failure could not be logged: {loggingException}");
        }
    }

}
