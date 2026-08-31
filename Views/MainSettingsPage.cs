using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using IDVBuff.Lifecycle;
using IDVBuff.PluginContracts;

namespace IDVBuff.Views;

/// <summary>The shell settings surface.</summary>
public sealed class MainSettingsPage : Page
{
    private readonly MainProgramPreferences _preferences = MainProgramPreferences.Load();

    public MainSettingsPage()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = CreateContent();
    }

    private FrameworkElement CreateContent()
    {
        var content = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(new TextBlock
        {
            Text = "设置",
            FontSize = 30,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = "常规",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        content.Children.Add(CreateToggleCard(
            "安全模式",
            "默认开启；不加载 CV、透明窗口或插件，也不允许以管理员权限运行。更改后重新启动 IDVB 生效",
            _preferences.SafeMode,
            async value =>
            {
                _preferences.SafeMode = value;
                if (value)
                {
                    // Safe mode and the elevated logon task are mutually exclusive, but a
                    // default/fresh safe-mode profile has no task to remove. Calling
                    // schtasks /Delete in that state returns a failure exit code and turns a
                    // successfully enabled safe mode into a misleading UAC-task error.
                    if (_preferences.StartWithWindows)
                        await ElevatedStartupTask.SetEnabledAsync(false);
                    _preferences.StartWithWindows = false;
                    var repository = new Features.Maps.MapRuntimeSettingsRepository();
                    var settings = await repository.LoadAsync();
                    settings.IsEnabled = false;
                    await repository.SaveAsync(settings);
                }
                _preferences.Save();
            }));
        content.Children.Add(CreateToggleCard(
            "开机自动启动",
            "登录 Windows 后以管理员权限自动启动 Identity Vision Bridge（启用时需确认一次 UAC）",
            !_preferences.SafeMode && _preferences.StartWithWindows,
            async value =>
            {
                if (value && _preferences.SafeMode)
                    throw new InvalidOperationException("安全模式下不能配置管理员权限启动。请先关闭安全模式并重新启动 IDVB。");
                await ElevatedStartupTask.SetEnabledAsync(value);
                _preferences.StartWithWindows = value;
                _preferences.Save();
            }));
        content.Children.Add(CreateToggleCard(
            "启动时最小化",
            "启动后直接进入系统托盘，不显示主窗口",
            _preferences.StartMinimized,
            value => SavePreferenceAsync(() => _preferences.StartMinimized = value)));
        content.Children.Add(CreateToggleCard(
            "最小化到系统托盘",
            "最小化或关闭主窗口时让 IDVB 在通知区域继续运行",
            _preferences.MinimizeToTray,
            value => SavePreferenceAsync(() => _preferences.MinimizeToTray = value)));

        content.Children.Add(new TextBlock
        {
            Text = "测绘",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        content.Children.Add(CreateToggleCard(
            "允许进入测绘模式",
            "启用后，对局控件才可以选择测绘模式；关闭时只能进入正常对局",
            _preferences.AllowSurveyMode,
            value => SavePreferenceAsync(() => _preferences.AllowSurveyMode = value)));

        content.Children.Add(new TextBlock
        {
            Text = "插件",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        content.Children.Add(CreateRandomDelaySafetyCard());

        content.Children.Add(new TextBlock
        {
            Text = "隐私",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        content.Children.Add(CreateToggleCard(
            "帮助我们改进模型",
            "开启后仅收集与地图识别、对齐和模型训练相关的脱敏数据，并每天最多上传一次训练包；可随时关闭",
            _preferences.HelpImproveModels,
            async value =>
            {
                await ModelImprovementPreferences.ApplyDataCollectionAsync(
                    value,
                    App.CurrentSession);
                _preferences.HelpImproveModels = value;
                _preferences.Save();
                if (value)
                    _ = ModelImprovementUploadService.TryUploadDailyAsync(_preferences);
            }));

        content.Children.Add(new TextBlock
        {
            Text = "开发者",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        content.Children.Add(CreateToggleCard(
            "开发者模式",
            "开启后，重新进入配置页面可使用“地图选择与自我训练”等高级菜单",
            _preferences.DeveloperMode,
            value => SavePreferenceAsync(() => _preferences.DeveloperMode = value)));

        content.Children.Add(new TextBlock
        {
            Text = "个性化",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        var (themeCard, themeSelector) = CreateThemeCard();
        content.Children.Add(CreateToggleCard(
            "跟随系统主题",
            "根据 Windows 的应用颜色模式自动切换浅色或深色主题",
            _preferences.FollowSystemTheme,
            value => SavePreferenceAsync(() =>
            {
                _preferences.FollowSystemTheme = value;
                themeSelector.IsEnabled = !value;
                ApplyColorTheme();
            })));
        content.Children.Add(themeCard);
        content.Children.Add(CreateToggleCard(
            "使用旧版主题",
            "下次启动 IDVB 时使用传统实色主题；颜色仍跟随上方的主题设置",
            _preferences.UseLegacyTheme,
            value => SavePreferenceAsync(() => _preferences.UseLegacyTheme = value)));

        return new Border
        {
            Margin = new Thickness(42, 34, 42, 64),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
    }

    private (Border Card, ComboBox Selector) CreateThemeCard()
    {
        var layout = new Grid { MinHeight = 86, Padding = new Thickness(26, 15, 24, 15) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = "应用主题",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock
        {
            Text = "关闭跟随系统主题后，可手动选择浅色或深色主题",
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);

        var selector = new ComboBox
        {
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_preferences.FollowSystemTheme
        };
        selector.Items.Add("浅色");
        selector.Items.Add("深色");
        selector.SelectedIndex = _preferences.UseDarkTheme ? 1 : 0;
        selector.SelectionChanged += (_, _) =>
        {
            var useDarkTheme = selector.SelectedIndex == 1;
            if (_preferences.UseDarkTheme == useDarkTheme)
                return;

            _preferences.UseDarkTheme = useDarkTheme;
            _preferences.Save();
            ApplyColorTheme();
        };
        Grid.SetColumn(selector, 1);
        layout.Children.Add(selector);

        return (new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        }, selector);
    }

    private void ApplyColorTheme() =>
        FluentTheme.ApplyColorTheme(_preferences.FollowSystemTheme, _preferences.UseDarkTheme);

    private Border CreateRandomDelaySafetyCard()
    {
        var layout = new Grid { MinHeight = 86, Padding = new Thickness(26, 15, 24, 15) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = "允许低延迟随机化",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock
        {
            Text = "解除插件随机延迟的默认安全下限，允许将上下限设置为 0 毫秒",
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);

        var toggle = new ToggleSwitch
        {
            IsOn = _preferences.AllowUnsafePluginRandomDelayMinimums,
            OnContent = string.Empty,
            OffContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Center
        };
        var updating = false;
        toggle.Toggled += async (_, _) =>
        {
            if (updating)
                return;
            toggle.IsEnabled = false;
            try
            {
                if (toggle.IsOn)
                {
                    var confirmed = await ConfirmUnsafeRandomDelayAsync();
                    if (!confirmed)
                    {
                        updating = true;
                        toggle.IsOn = false;
                        updating = false;
                        return;
                    }
                }

                _preferences.AllowUnsafePluginRandomDelayMinimums = toggle.IsOn;
                _preferences.Save();
                PluginRandomDelayPolicy.AllowUnsafeMinimums = toggle.IsOn;
            }
            catch (Exception exception)
            {
                updating = true;
                toggle.IsOn = _preferences.AllowUnsafePluginRandomDelayMinimums;
                updating = false;
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "设置未保存",
                    Content = exception.Message,
                    CloseButtonText = "知道了"
                }.ShowAsync();
            }
            finally
            {
                toggle.IsEnabled = true;
            }
        };
        Grid.SetColumn(toggle, 1);
        layout.Children.Add(toggle);
        return new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }

    private async Task<bool> ConfirmUnsafeRandomDelayAsync()
    {
        var destructiveStyle = new Style(typeof(Button));
        destructiveStyle.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Colors.Firebrick)));
        destructiveStyle.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Colors.White)));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "降低随机延迟可能带来风险",
            Content = new TextBlock
            {
                Text = "启用后，插件可将随机等待范围调至默认安全值以下。过低的操作间隔可能更容易触发异常行为检测，并带来账号处罚或封禁风险。请确认你已理解风险并谨慎设置。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            },
            PrimaryButtonText = "请等待 3 秒",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false,
            PrimaryButtonStyle = destructiveStyle,
            DefaultButton = ContentDialogButton.Close
        };
        using var countdownCancellation = new CancellationTokenSource();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                for (var seconds = 3; seconds > 0; seconds--)
                {
                    dialog.PrimaryButtonText = $"请等待 {seconds} 秒";
                    await Task.Delay(TimeSpan.FromSeconds(1), countdownCancellation.Token);
                }
                dialog.PrimaryButtonText = "确认关闭限制";
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
        };
        var result = await dialog.ShowAsync();
        countdownCancellation.Cancel();
        return result == ContentDialogResult.Primary;
    }

    private Border CreateToggleCard(
        string title,
        string description,
        bool isOn,
        Func<bool, Task> changed)
    {
        var layout = new Grid { MinHeight = 86, Padding = new Thickness(26, 15, 24, 15) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);

        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = string.Empty,
            OffContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = true
        };
        var updating = false;
        toggle.Toggled += async (_, _) =>
        {
            if (updating || toggle.IsOn == isOn)
                return;
            var requestedValue = toggle.IsOn;
            toggle.IsEnabled = false;
            try
            {
                await changed(requestedValue);
                isOn = requestedValue;
            }
            catch (Exception exception)
            {
                updating = true;
                toggle.IsOn = isOn;
                updating = false;
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "设置未保存",
                    Content = exception.Message,
                    CloseButtonText = "知道了"
                }.ShowAsync();
            }
            finally
            {
                toggle.IsEnabled = true;
            }
        };
        Grid.SetColumn(toggle, 1);
        layout.Children.Add(toggle);

        return new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }

    private Task SavePreferenceAsync(Action update)
    {
        update();
        _preferences.Save();
        return Task.CompletedTask;
    }
}
