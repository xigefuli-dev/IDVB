using System.Diagnostics;
using System.Text.Json;
using IDVBuff.PluginContracts;
using IDVBuff.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Features.Plugins;
/// <summary>
/// 插件设置 TeachingTip 管理器（TTM）。统一管理「插件设置页」的弹出、持久化
/// 与摘除生命周期，避免跨线程 / 重复打开 / 页面卸载 / 插件禁用等生命周期串线
/// 导致点击设置时卡死崩溃。
///
/// 设计要点：
/// - 每次 <see cref="ShowSettings"/> 创建全新 <see cref="TeachingTip"/> 实例，
///   彻底绕开重开动画 / 卸载重开的已知崩溃点，天然满足「同一时刻只开一个」。
/// - 打开前先从 <see cref="PluginPreferencesStore"/> 恢复持久化值并写回插件
///   内存态，再按描述符渲染控件，全部「先取值后订阅事件」。
/// - 关闭后通过 <c>Closed</c> 事件摘除实例，宿主面板绝不累积旧实例。
/// </summary>
public sealed partial class TeachingTipManager
{

    private FrameworkElement BuildKeyBindingControl(
        IPluginSettingsProvider provider,
        string pluginId,
        PluginKeyBindingSetting setting)
    {
        return CreateInputBindingControl(
            ReadProviderValue(provider, setting) as string,
            setting.DefaultValue, setting.AllowedKinds, _dispatcher,
            next => PersistSetting(provider, pluginId, setting.Key,
                JsonSerializer.SerializeToElement(next.StorageValue)));
    }

    internal static FrameworkElement CreateInputBindingControl(
        string? current, string? defaultValue, PluginInputBindingKinds allowedKinds,
        DispatcherQueue dispatcher, Action<PluginInputBinding> save)
    {
        if (!PluginInputBinding.TryParse(
                current,
                allowedKinds,
                out var binding))
        {
            PluginInputBinding.TryParse(
                defaultValue,
                allowedKinds,
                out binding);
        }

        var recording = false;
        var recordingHeldKeys = new HashSet<uint>();
        uint recordingTriggerKey = 0;
        var hovered = false;
        var ignoreNextClick = false;
        var xButton1WasDown = false;
        var xButton2WasDown = false;
        var sideButtonPoller = dispatcher.CreateTimer();
        sideButtonPoller.Interval = TimeSpan.FromMilliseconds(15);
        var host = new Grid
        {
            IsTabStop = true,
            Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(1, 0, 0, 0))
        };
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 200
        };
        var bindingDescription = new TextBlock
        {
            FontSize = 12,
            Foreground = FluentTheme.Brush(host, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        host.Children.Add(button);
        var control = new StackPanel { Spacing = 6 };
        control.Children.Add(bindingDescription);
        control.Children.Add(host);
        control.Children.Add(new TextBlock
        {
            Text = "支持鼠标左键、右键、中键及侧键 1 / 2。G502 等鼠标的额外功能键，请先在驱动中分配为键盘键（如 F13–F24），再点击设置按键录入。",
            FontSize = 12,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        void RefreshButton()
        {
            bindingDescription.Text = binding.IsConfigured
                ? $"当前绑定：{binding.DisplayName}"
                : "当前未绑定";
            RefreshPluginBindingButtonAppearance(button, binding, recording, hovered);
        }

        button.PointerEntered += (_, _) =>
        {
            hovered = true;
            RefreshButton();
        };
        button.PointerExited += (_, _) =>
        {
            hovered = false;
            RefreshButton();
        };

        async Task SaveBinding(PluginInputBinding next)
        {
            sideButtonPoller.Stop();
            recording = false;
            recordingHeldKeys.Clear();
            recordingTriggerKey = 0;
            binding = next;
            save(binding);
            RefreshButton();
            await Task.CompletedTask;
        }

        button.Click += (_, _) =>
        {
            if (ignoreNextClick)
            {
                ignoreNextClick = false;
                return;
            }
            if (recording)
                return;
            if (binding.IsConfigured)
            {
                _ = SaveBinding(new PluginInputBinding());
                return;
            }

            recording = true;
            recordingHeldKeys.Clear();
            recordingTriggerKey = 0;
            xButton1WasDown = IsRecordingMouseKeyDown(0x05);
            xButton2WasDown = IsRecordingMouseKeyDown(0x06);
            sideButtonPoller.Start();
            RefreshButton();
            host.Focus(FocusState.Programmatic);
        };

        sideButtonPoller.Tick += async (_, _) =>
        {
            if (!recording
                || (allowedKinds & PluginInputBindingKinds.Mouse) == 0)
            {
                return;
            }

            var xButton1IsDown =
                IsRecordingMouseKeyDown(0x05);
            var xButton2IsDown =
                IsRecordingMouseKeyDown(0x06);
            if (xButton1IsDown && !xButton1WasDown)
            {
                await SaveBinding(
                    PluginInputBinding.Mouse(PluginMouseButton.XButton1));
                return;
            }
            if (xButton2IsDown && !xButton2WasDown)
            {
                await SaveBinding(
                    PluginInputBinding.Mouse(PluginMouseButton.XButton2));
                return;
            }
            xButton1WasDown = xButton1IsDown;
            xButton2WasDown = xButton2IsDown;
        };
        host.Unloaded += (_, _) =>
        {
            recording = false;
            sideButtonPoller.Stop();
        };

        host.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(async (_, args) =>
        {
            if (!recording)
                return;
            args.Handled = true;
            var key = (uint)args.Key;
            if (!recordingHeldKeys.Add(key))
                return;
            recordingTriggerKey = key;
        }), handledEventsToo: true);

        host.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(async (_, args) =>
        {
            if (!recording)
            {
                return;
            }
            args.Handled = true;
            var key = (uint)args.Key;
            if (!recordingHeldKeys.Contains(key))
                return;
            if ((allowedKinds & PluginInputBindingKinds.Keyboard) != 0)
            {
                await SaveBinding(CreatePluginKeyboardBinding(
                    recordingTriggerKey,
                    recordingHeldKeys.Where(held => held != recordingTriggerKey)));
            }
        }), handledEventsToo: true);

        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(async (_, args) =>
        {
            if (!recording
                || (allowedKinds & PluginInputBindingKinds.Mouse) == 0)
            {
                return;
            }

            var properties = args.GetCurrentPoint(host).Properties;
            if (!TryGetPluginMouseButton(properties, out var mouseButton))
                return;
            ignoreNextClick = mouseButton == PluginMouseButton.Left;
            args.Handled = true;
            await SaveBinding(PluginInputBinding.Mouse(mouseButton));
        }), handledEventsToo: true);

        RefreshButton();
        return control;
    }

    /// <summary>
    /// 将控件新值同时写回插件内存态与存储层。任一失败只记日志，不影响 UI。
    /// </summary>
    private void PersistSetting(
        IPluginSettingsProvider provider, string pluginId, string key, JsonElement element)
    {
        var clr = element.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => (object?)element.GetBoolean(),
            JsonValueKind.Number => element.TryGetDouble(out var d) ? (object?)d : null,
            JsonValueKind.String => element.GetString(),
            _ => null
        };
        if (clr is not null)
            SafeSetProviderValue(provider, key, clr);
        try
        {
            _store.SetSetting(pluginId, key, element);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TTM 持久化设置失败 {pluginId}/{key}: {exception}");
        }
    }

}
