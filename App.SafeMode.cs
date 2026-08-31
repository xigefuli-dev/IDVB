using IDVBuff.Features.Maps;
using IDVBuff.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using IDVBuff.Views;

namespace IDVBuff;

public partial class App
{
    private MapGlobalInputService? _safeModeInput;
    private static bool _isSafeMode = true;
    public static bool IsSafeMode
    {
        get => _isSafeMode;
        private set
        {
            _isSafeMode = value;
            Environment.SetEnvironmentVariable("IDVB_SAFE_MODE", value ? "1" : "0");
        }
    }

    private async Task<bool> TryCompleteSafeModeLaunchAsync(
        bool startMinimized,
        MainProgramPreferences preferences)
    {
        if (!IsSafeMode)
            return false;

        WriteStartupTrace("Safe mode is active; CV, overlay and plugin runtimes will not be initialized.");
        if (!preferences.SafeModeFirstRunIntroductionCompleted)
        {
            if (startMinimized)
                ShowMainWindow();
            await ShowSafeModeFirstRunIntroductionAsync(preferences);
        }
        if (GameProcessIntegrityService.Check().CurrentProcessIsElevated)
        {
            if (startMinimized)
                ShowMainWindow();
            await ShowSafeModeElevationWarningAsync();
        }

        await EnsureMapRuntimeDisabledAsync();
        await InitializeSafeModeTraditionalWindowInputAsync(
            DispatcherQueue.GetForCurrentThread());
        AutomaticUpdateLauncher.TryLaunch();
        if (startMinimized)
        {
            HideMainWindow();
            SetMainWindowCloaked(false);
        }
        return true;
    }

    private async Task ShowSafeModeFirstRunIntroductionAsync(MainProgramPreferences preferences)
    {
        var xamlRoot = await WaitForMainXamlRootAsync();
        if (xamlRoot is null)
            return;

        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "关于 Identity Vision Bridge",
            Content = "IDVB 是免费的开源软件，绝对不存在任何收费行为。如果你是花钱购买的，说明你被骗了。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();

        var choice = await ShowSafeModeChoiceAsync(xamlRoot);
        preferences.SafeModeFirstRunIntroductionCompleted = true;
        if (!choice)
        {
            preferences.Save();
            return;
        }

        preferences.SafeMode = false;
        preferences.Save();
        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "安全模式已关闭",
            Content = "已关闭，接下来需要你以管理员权限重新启动此软件。",
            CloseButtonText = "好的",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
        RequestApplicationExit();
    }

    private async Task<XamlRoot?> WaitForMainXamlRootAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (window?.Content is FrameworkElement { XamlRoot: { } xamlRoot })
                return xamlRoot;

            await Task.Delay(50);
        }

        WriteStartupTrace("Safe-mode first-run introduction could not acquire a XamlRoot.");
        return null;
    }

    private static async Task<bool> ShowSafeModeChoiceAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "安全模式",
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMaxWidth"] = 560d;

        var keepCurrentButton = CreateSafeModeChoiceButton("保持现状", isPrimary: false);
        var disableSafeModeButton = CreateSafeModeChoiceButton("帮我关闭安全模式", isPrimary: true);
        keepCurrentButton.IsEnabled = false;
        disableSafeModeButton.IsEnabled = false;

        var disableSafeMode = false;
        keepCurrentButton.Click += (_, _) => dialog.Hide();
        disableSafeModeButton.Click += (_, _) =>
        {
            disableSafeMode = true;
            dialog.Hide();
        };

        var waitHint = new TextBlock
        {
            Text = "请阅读说明，5 秒后可选择。",
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            FontSize = 13
        };
        var actions = new Grid { ColumnSpacing = 12 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(keepCurrentButton, 0);
        Grid.SetColumn(disableSafeModeButton, 1);
        actions.Children.Add(keepCurrentButton);
        actions.Children.Add(disableSafeModeButton);
        dialog.Content = new StackPanel
        {
            MinWidth = 440,
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "IDVB 当前处于安全模式，高级功能已被禁用。如果你想要使用这些功能，可以在“主设置 - 安全模式”中关闭安全模式，然后重新以管理员权限启动 IDVB。\n\n关闭安全模式后，你可以正常使用自动识别、自动对齐、游戏内显示层、插件等功能；开启时，你只能自己筛选地图并以普通窗口的形式展示。\n\n想要退出安全模式吗？\n你随时可以重新启用安全模式。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15
                },
                waitHint,
                actions
            }
        };

        var secondsRemaining = 5;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            secondsRemaining--;
            if (secondsRemaining > 0)
            {
                waitHint.Text = $"请阅读说明，{secondsRemaining} 秒后可选择。";
                return;
            }

            timer.Stop();
            waitHint.Text = "你现在可以选择。";
            keepCurrentButton.IsEnabled = true;
            disableSafeModeButton.IsEnabled = true;
        };
        timer.Start();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            timer.Stop();
        }
        return disableSafeMode;
    }

    private static Button CreateSafeModeChoiceButton(string text, bool isPrimary)
    {
        var background = isPrimary
            ? FluentTheme.Brush("AccentFillColorDefaultBrush")
            : FluentTheme.Brush("ControlFillColorDefaultBrush");
        var foreground = isPrimary
            ? FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
            : FluentTheme.Brush("TextFillColorPrimaryBrush");
        var button = new Button
        {
            Content = text,
            MinHeight = 40,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = background,
            Foreground = foreground
        };
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        return button;
    }

    private async Task InitializeSafeModeTraditionalWindowInputAsync(
        DispatcherQueue dispatcher)
    {
        var settings = await new MapRuntimeSettingsRepository().LoadAsync();
        _safeModeInput = new MapGlobalInputService(dispatcher);
        _safeModeInput.SwitchFloorInvoked += SafeModeSwitchFloorInvoked;
        ApplySafeModeTraditionalWindowBinding(settings.TraditionalWindowSwitchFloorBinding);
    }

    internal static void UpdateSafeModeTraditionalWindowBinding(MapInputBinding binding) =>
        _currentApp?.ApplySafeModeTraditionalWindowBinding(binding);

    private void ApplySafeModeTraditionalWindowBinding(MapInputBinding binding)
    {
        _safeModeInput?.ApplyBindings(
            new MapInputBinding(), new MapInputBinding(), new MapInputBinding(),
            new MapInputBinding(), new MapInputBinding(), binding,
            new MapInputBinding(), new MapInputBinding());
    }

    private static void SafeModeSwitchFloorInvoked(object? sender, MapInputInvokedEventArgs e) =>
        DirectMapDisplayWindow.SwitchFloorForOpenWindows();

    private void DisposeSafeModeTraditionalWindowInput()
    {
        if (_safeModeInput is null)
            return;
        _safeModeInput.SwitchFloorInvoked -= SafeModeSwitchFloorInvoked;
        _safeModeInput.Dispose();
        _safeModeInput = null;
    }

    private static async Task EnsureMapRuntimeDisabledAsync()
    {
        var repository = new MapRuntimeSettingsRepository();
        var settings = await repository.LoadAsync();
        if (!settings.IsEnabled)
            return;
        settings.IsEnabled = false;
        await repository.SaveAsync(settings);
    }

    private async Task ShowSafeModeElevationWarningAsync()
    {
        if (window?.Content is not FrameworkElement root || root.XamlRoot is null)
            return;

        await new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "安全模式不需要管理员权限",
            Content = "当前 Identity Vision Bridge 正以管理员权限运行。安全模式不需要管理员权限，建议退出后以普通用户权限重新启动；你也可以关闭此提示并继续使用。",
            CloseButtonText = "继续使用",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }
}
