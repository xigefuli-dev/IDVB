using System.Diagnostics;
using IDVBuff.Features.GameLaunch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class HomePage : Page
{
    private bool _gameLaunchInProgress;

    private Button CreateLaunchGameButton()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _launchGameIcon, _launchGameLabel }
        };
        var button = new Button
        {
            Width = 300,
            Height = 58,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = content,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            CornerRadius = new CornerRadius(8),
            Shadow = new ThemeShadow()
        };
        button.Click += LaunchGameButton_Click;
        return button;
    }

    private async void LaunchGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameLaunchInProgress) return;
        _gameLaunchInProgress = true;
        try { await LaunchOfficialGameAsync(); }
        finally { _gameLaunchInProgress = false; UpdateGameStatus(); }
    }

    private async Task LaunchOfficialGameAsync()
    {
        if (IsGameRunning())
            return;

        _launchGameButton.IsEnabled = false;
        try
        {
            var manager = App.Plugins;
            if (manager?.IsEnabled(IDVBuff.Plugins.IdvLogin.IdvLoginPlugin.PluginId) == true &&
                manager.TryGet(IDVBuff.Plugins.IdvLogin.IdvLoginPlugin.PluginId, out var registered) &&
                registered is IDVBuff.Plugins.IdvLogin.IdvLoginPlugin loginPlugin)
            {
                try
                {
                    await loginPlugin.StopIfRunningAsync(default);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HomePage] 停止 idv-login 非致命异常: {ex.Message}");
                }
            }
        }
        finally { UpdateGameStatus(); }

        try
        {
            await FeverAccountStore.Instance.EnsureActiveAccountDeployedAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomePage] 部署当前激活官服账号异常: {ex.Message}");
        }

        var activeAcc = FeverAccountStore.Instance.GetActiveAccount();
        if (activeAcc is not null && activeAcc.IsLongTerm)
        {
            _launchGameLabel.Text = "···启动中";
        }

        if (!FeverGamesGameLauncher.TryLaunch(out var failureReason, activeAcc))
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法启动游戏",
                Content = failureReason,
                CloseButtonText = "知道了"
            }.ShowThemedAsync();
            return;
        }

        UpdateGameStatus();
    }

    private void UpdateGameStatus()
    {
        var running = IsGameRunning();
        _launchGameLabel.Text = running ? "···游戏中" : "启动游戏";
        _launchGameIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _launchGameButton.IsEnabled = !running && !_gameLaunchInProgress;
    }

    private static bool IsGameRunning() => Process.GetProcessesByName("dwrg").Length > 0;
}
