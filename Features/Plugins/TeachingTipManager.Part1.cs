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
        var current = ReadProviderValue(provider, setting) as string;
        if (!PluginInputBinding.TryParse(
                current,
                setting.AllowedKinds,
                out var binding))
        {
            PluginInputBinding.TryParse(
                setting.DefaultValue,
                setting.AllowedKinds,
                out binding);
        }

        var recording = false;
        var modifiers = PluginInputModifiers.None;
        var hovered = false;
        var ignoreNextClick = false;
        var xButton1WasDown = false;
        var xButton2WasDown = false;
        var sideButtonPoller = _dispatcher.CreateTimer();
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
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        host.Children.Add(button);
        var control = new StackPanel { Spacing = 6 };
        control.Children.Add(bindingDescription);
        control.Children.Add(host);

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
            modifiers = PluginInputModifiers.None;
            binding = next;
            PersistSetting(
                provider,
                pluginId,
                setting.Key,
                JsonSerializer.SerializeToElement(binding.StorageValue));
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
            modifiers = PluginInputModifiers.None;
            xButton1WasDown = IsCurrentKeyDown((Windows.System.VirtualKey)0x05);
            xButton2WasDown = IsCurrentKeyDown((Windows.System.VirtualKey)0x06);
            sideButtonPoller.Start();
            RefreshButton();
            host.Focus(FocusState.Programmatic);
        };

        sideButtonPoller.Tick += async (_, _) =>
        {
            if (!recording
                || (setting.AllowedKinds & PluginInputBindingKinds.Mouse) == 0)
            {
                return;
            }

            var xButton1IsDown =
                IsCurrentKeyDown((Windows.System.VirtualKey)0x05);
            var xButton2IsDown =
                IsCurrentKeyDown((Windows.System.VirtualKey)0x06);
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
        host.Unloaded += (_, _) => sideButtonPoller.Stop();

        host.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(async (_, args) =>
        {
            if (!recording)
                return;
            args.Handled = true;
            if (TryGetPluginModifier(args.Key, out var modifier))
            {
                modifiers |= modifier;
                return;
            }

            var next = PluginInputBinding.Keyboard(
                (uint)args.Key,
                ReadCurrentPluginModifiers(modifiers));
            if ((setting.AllowedKinds & PluginInputBindingKinds.Keyboard) != 0)
                await SaveBinding(next);
        }), handledEventsToo: true);

        host.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(async (_, args) =>
        {
            if (!recording
                || !TryGetPluginModifier(args.Key, out var modifier))
            {
                return;
            }
            args.Handled = true;
            if ((modifiers & modifier) == 0)
                return;
            modifiers = PluginInputModifiers.None;
            if ((setting.AllowedKinds & PluginInputBindingKinds.Keyboard) != 0)
            {
                await SaveBinding(PluginInputBinding.Keyboard(
                    (uint)args.Key));
            }
        }), handledEventsToo: true);

        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(async (_, args) =>
        {
            if (!recording
                || (setting.AllowedKinds & PluginInputBindingKinds.Mouse) == 0)
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
