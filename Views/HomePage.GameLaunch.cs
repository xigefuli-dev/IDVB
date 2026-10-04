using System.Diagnostics;
using IDVBuff.Features.GameLaunch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
    private bool _gameStatusUpdating;
    private bool? _gameRunning;
    private int _gameStatusGeneration;
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
            Background = FluentTheme.Brush(this, "AccentFillColorDefaultBrush"),
            Foreground = FluentTheme.Brush(this, "TextOnAccentFillColorPrimaryBrush"),
            CornerRadius = new CornerRadius(8),
            Shadow = new ThemeShadow()
        };
        button.Click += LaunchGameButton_Click;
        return button;
    }

    private async void LaunchGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (await Task.Run(IsGameRunning))
            return;

        if (!FeverGamesGameLauncher.TryLaunch(out var failureReason))
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

    private async void UpdateGameStatus()
    {
        if (_gameStatusUpdating) return;
        _gameStatusUpdating = true;
        var generation = _gameStatusGeneration;
        try
        {
            var running = await Task.Run(IsGameRunning);
            if (!IsLoaded || generation != _gameStatusGeneration || _gameRunning == running) return;
            _gameRunning = running;
            _launchGameLabel.Text = running ? "···游戏中" : "启动游戏";
            _launchGameIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            _launchGameButton.Background = FluentTheme.Brush(this, running
                ? "ControlFillColorDisabledBrush"
                : "AccentFillColorDefaultBrush");
            _launchGameButton.Opacity = running ? 0.72 : 1;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[HomePage] 游戏状态查询失败: {exception.Message}");
        }
        finally { _gameStatusUpdating = false; }
    }

    private static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("dwrg");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

}
