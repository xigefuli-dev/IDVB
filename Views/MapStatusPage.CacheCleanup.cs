using IDVBuff.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage : UserControl
{
    private Button CreateCacheCleanupButton()
    {
        var button = new Button
        {
            Content = "清理缓存", MinWidth = 112, MinHeight = 42,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(255, 245, 166, 35)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 32, 0))
        };
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 255, 186, 65));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 222, 143, 20));
        button.Resources["ButtonForegroundPointerOver"] = button.Foreground;
        button.Resources["ButtonForegroundPressed"] = button.Foreground;
        button.Click += CacheCleanup_Click;
        return button;
    }

    private async void CacheCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled) return;
        var dialogRoot = XamlRoot;
        if (dialogRoot is null) return;
        button.IsEnabled = false;
        button.Content = "正在计算…";
        try
        {
            using var workflow = await LocalCacheCleanup.BeginManualAsync();
            var plan = await Task.Run(() => LocalCacheCleanup.Scan(AppDataPaths.RootDirectory,
                activePaths: SavedDiagnosticDataRetention.ActivePaths));
            if (XamlRoot != dialogRoot) return;
            var summary = string.Join("\n", plan.Entries.GroupBy(entry => entry.Category)
                .Select(group => $"{group.Key}：{FormatCacheBytes(group.Sum(entry => entry.Bytes))}"));
            if (plan.FileCount == 0)
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "清理缓存", CloseButtonText = "知道了",
                    Content = "计算完成，当前没有可清理的缓存。正在使用的数据会保留。"
                }.ShowAsync();
                return;
            }
            button.Content = "等待确认…";
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "是否清理缓存？",
                Content = $"计算完成，可清理 {FormatCacheBytes(plan.Bytes)}（{plan.FileCount:N0} 个文件）。\n\n{summary}\n\n保留地图、配置、手动保存的缩放缓存、发布密钥、当前发布包和正在使用的数据。结构缓存会在需要时重新生成。\n\n确认清理以上缓存？",
                PrimaryButtonText = "清理", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            button.Content = "正在清理…";
            var result = await Task.Run(() => LocalCacheCleanup.Execute(plan, SavedDiagnosticDataRetention.ActivePaths));
            if (XamlRoot != dialogRoot) return;
            var message = $"已清理 {FormatCacheBytes(result.Bytes)}（{result.DeletedFiles:N0} 个文件）。";
            if (result.AlreadyMissingFiles > 0)
                message += $"\n另有 {FormatCacheBytes(result.AlreadyMissingBytes)}（{result.AlreadyMissingFiles:N0} 个文件）已不存在，无需重复清理，未计入本次清理量。";
            if (result.SkippedFiles > 0)
                message += $"\n{result.SkippedFiles:N0} 个文件已变化或正在使用，已保留。";
            if (result.Failures.Count > 0)
                message += $"\n{result.Failures.Count:N0} 项未能清理：\n" + string.Join("\n", result.Failures.Take(5));
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = result.Failures.Count == 0 ? "缓存清理完成" : "部分缓存未能清理",
                Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock
                    { Text = message, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "知道了"
            }.ShowAsync();
        }
        catch (Exception error)
        {
            if (XamlRoot != dialogRoot) return;
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "缓存清理未完成",
                Content = $"未能完成计算或清理：{error.Message}", CloseButtonText = "知道了"
            }.ShowAsync();
        }
        finally
        {
            button.Content = "清理缓存";
            button.IsEnabled = true;
        }
    }

    private static string FormatCacheBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):F2} GiB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):F2} MiB"
        : bytes >= 1024 ? $"{bytes / 1024d:F2} KiB" : $"{bytes} B";
}
