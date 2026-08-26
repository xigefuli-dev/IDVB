using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

public sealed class SafeModeMapStatusPage : UserControl
{
    private readonly MapRuntimeSettingsRepository _settingsRepository = new();
    private MapRuntimeSettings? _settings;
    private readonly TextBlock _bindingValue = new();
    private readonly TextBlock _status = new() { FontSize = 13 };
    private readonly Button _bindingButton = new();
    private Grid? _root;
    private bool _recording;
    private bool _hovered;
    private MapInputModifiers _recordingModifiers;

    public SafeModeMapStatusPage()
    {
        var toggle = new ToggleSwitch
        {
            Header = "总开关", OffContent = "已关闭", OnContent = "已启动", IsOn = false
        };
        var reverting = false;
        toggle.Toggled += async (_, _) =>
        {
            if (reverting || !toggle.IsOn) return;
            reverting = true;
            toggle.IsOn = false;
            reverting = false;
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "暂时无法开启",
                Content = "请先关闭安全模式、完成全部按键绑定，并至少添加一张地图。关闭安全模式后需要重新启动 IDVB。",
                CloseButtonText = "知道了"
            }.ShowAsync();
        };

        var content = new StackPanel { Margin = new Thickness(42, 36, 42, 64), Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = "加页手记 · 配置", FontSize = 30,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(toggle);
        content.Children.Add(new TextBlock
        {
            Text = "安全模式下不会加载 CV、透明窗口或插件组件。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 12, 0, 4),
            Background = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128))
        });
        content.Children.Add(new TextBlock
        {
            Text = "按键绑定", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(CreateBindingRow());
        content.Children.Add(_status);

        _root = new Grid { IsTabStop = true };
        _root.Children.Add(content);
        _root.KeyDown += Root_KeyDown;
        _root.KeyUp += Root_KeyUp;
        _root.PointerPressed += Root_PointerPressed;
        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Loaded += SafeModeMapStatusPage_Loaded;
    }

    private async void SafeModeMapStatusPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
            return;
        try
        {
            _settings = await _settingsRepository.LoadAsync();
            Refresh();
        }
        catch (Exception exception)
        {
            _status.Text = $"按键绑定加载失败：{exception.Message}";
        }
    }

    private UIElement CreateBindingRow()
    {
        var row = new Grid { ColumnSpacing = 18 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = "切换楼层 · 传统窗口", FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = "切换“直接显示”传统窗口中展示的地图楼层。",
            FontSize = 13, TextWrapping = TextWrapping.Wrap
        });
        _bindingValue.FontSize = 13;
        text.Children.Add(_bindingValue);
        row.Children.Add(text);

        _bindingButton.Content = "设置按键";
        _bindingButton.MinWidth = 98;
        _bindingButton.MinHeight = 38;
        _bindingButton.CornerRadius = new CornerRadius(7);
        _bindingButton.Click += BindingButton_Click;
        _bindingButton.PointerEntered += (_, _) => { _hovered = true; Refresh(); };
        _bindingButton.PointerExited += (_, _) => { _hovered = false; Refresh(); };
        Grid.SetColumn(_bindingButton, 1);
        row.Children.Add(_bindingButton);
        return row;
    }

    private async void BindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
            return;
        if (!_settings.TraditionalWindowSwitchFloorBinding.IsConfigured)
        {
            _recording = true;
            _recordingModifiers = MapInputModifiers.None;
            _status.Text = "请按下用于切换传统窗口楼层的键盘或鼠标按键。";
            _root?.Focus(FocusState.Programmatic);
            Refresh();
            return;
        }
        await SaveAsync(new MapInputBinding());
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        if (TryGetModifier(e.Key, out var modifier))
        {
            _recordingModifiers |= modifier;
            return;
        }
        var binding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = (uint)e.Key,
            Modifiers = _recordingModifiers
        };
        _recordingModifiers = MapInputModifiers.None;
        _ = SaveAsync(binding);
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording || !TryGetModifier(e.Key, out var modifier)
            || (_recordingModifiers & modifier) == 0) return;
        e.Handled = true;
        _recordingModifiers = MapInputModifiers.None;
        _ = SaveAsync(new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard, VirtualKey = (uint)e.Key
        });
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_recording) return;
        var properties = e.GetCurrentPoint(_root).Properties;
        var button = properties.IsLeftButtonPressed ? MapMouseButton.Left
            : properties.IsRightButtonPressed ? MapMouseButton.Right
            : properties.IsMiddleButtonPressed ? MapMouseButton.Middle
            : properties.IsXButton1Pressed ? MapMouseButton.XButton1
            : MapMouseButton.XButton2;
        e.Handled = true;
        _ = SaveAsync(new MapInputBinding
        {
            Kind = MapInputBindingKind.Mouse, MouseButton = button
        });
    }

    private async Task SaveAsync(MapInputBinding binding)
    {
        _recording = false;
        _recordingModifiers = MapInputModifiers.None;
        try
        {
            if (_settings is null)
                return;
            _settings.TraditionalWindowSwitchFloorBinding = binding.Clone();
            await _settingsRepository.SaveAsync(_settings);
            App.UpdateSafeModeTraditionalWindowBinding(binding);
            _status.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        Refresh();
    }

    private void Refresh()
    {
        _bindingButton.IsEnabled = _settings is not null;
        if (_settings is null)
        {
            _bindingValue.Text = "正在加载…";
            return;
        }
        var binding = _settings.TraditionalWindowSwitchFloorBinding;
        _bindingValue.Text = $"当前：{binding.DisplayName}";
        var showReset = !_recording && binding.IsConfigured && _hovered;
        _bindingButton.Content = _recording ? "请按按键…" : showReset ? "重置按键" : "设置按键";
        _bindingButton.Background = new SolidColorBrush(_recording
            ? Color.FromArgb(255, 22, 62, 115)
            : !binding.IsConfigured ? Color.FromArgb(255, 46, 132, 225)
            : showReset ? Color.FromArgb(255, 196, 55, 55)
            : Color.FromArgb(255, 242, 242, 242));
        _bindingButton.Foreground = new SolidColorBrush(
            _recording || !binding.IsConfigured || showReset
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 32, 32, 32));
    }

    private static bool TryGetModifier(VirtualKey key, out MapInputModifiers modifier)
    {
        modifier = (uint)key switch
        {
            0x10 or 0xA0 or 0xA1 => MapInputModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => MapInputModifiers.Control,
            0x12 or 0xA4 or 0xA5 => MapInputModifiers.Alt,
            0x5B or 0x5C => MapInputModifiers.Windows,
            _ => MapInputModifiers.None
        };
        return modifier != MapInputModifiers.None;
    }
}
