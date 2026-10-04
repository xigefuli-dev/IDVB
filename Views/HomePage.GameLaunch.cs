using System.Diagnostics;
using IDVBuff.Features.GameLaunch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
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
        if (IsGameRunning())
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

    private void UpdateGameStatus()
    {
        var running = IsGameRunning();
        _launchGameLabel.Text = running ? "···游戏中" : "启动游戏";
        _launchGameIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _launchGameButton.Background = FluentTheme.Brush(this, running
            ? "ControlFillColorDisabledBrush"
            : "AccentFillColorDefaultBrush");
        _launchGameButton.Opacity = running ? 0.72 : 1;
    }

    private static bool IsGameRunning() => Process.GetProcessesByName("dwrg").Length > 0;

}
