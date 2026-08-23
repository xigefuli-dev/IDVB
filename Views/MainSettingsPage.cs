using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IDVBuff.Lifecycle;

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
            "开机自动启动",
            "登录 Windows 后以管理员权限自动启动 Identity Vision Bridge（启用时需确认一次 UAC）",
            _preferences.StartWithWindows,
            async value =>
            {
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
            Text = "个性化",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        content.Children.Add(CreateToggleCard(
            "使用旧版主题",
            "下次启动 IDVB 时使用传统实色主题，不再使用亚克力背景",
            _preferences.UseLegacyTheme,
            value => SavePreferenceAsync(() => _preferences.UseLegacyTheme = value)));

        return new Border
        {
            Margin = new Thickness(42, 34, 42, 64),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
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
