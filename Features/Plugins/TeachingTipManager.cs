using System.Diagnostics;
using System.Text.Json;
using IDVBuff.PluginContracts;
using IDVBuff.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private const double TipMinWidth = 300;
    private const double TipMinHeight = 260;
    private const double TipMaxHeight = 340;
    private const double ContentMaxHeight = 320;

    private readonly DispatcherQueue _dispatcher;
    private readonly PluginPreferencesStore _store;

    private Panel? _host;
    private TeachingTip? _tip;
    private Grid? _dismissLayer;
    private IPlugin? _currentPlugin;
    // IsOpen 置 true 的时刻；入场动画结束前禁止关闭（用户要求）。long.MinValue 表示未打开。
    private long _openedAt = long.MinValue;
    // TeachingTip 无 Opened 事件（microsoft-ui-xaml#1607），用定时近似入场动画结束。
    // 打开动画是 Storyboard 定时动画，时长固定，400ms 覆盖典型动画时长。
    private static readonly long OpenAnimationTicks =
        (long)(400 * Stopwatch.Frequency / 1000.0);

    public TeachingTipManager(DispatcherQueue dispatcher, PluginPreferencesStore store)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>当前打开设置页的插件 Id；未打开为 null。</summary>
    public string? CurrentPluginId => _currentPlugin?.Id;

    /// <summary>指定插件的设置页是否正处于打开状态。</summary>
    public bool IsShowing(string pluginId) =>
        !string.IsNullOrEmpty(pluginId)
        && _currentPlugin is not null
        && string.Equals(_currentPlugin.Id, pluginId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 挂载宿主面板（插件页 root Grid）。若已挂载到其他面板则先摘除旧实例。
    /// </summary>
    public void Attach(Panel host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!_dispatcher.HasThreadAccess)
        {
            _ = _dispatcher.TryEnqueue(() => Attach(host));
            return;
        }

        if (ReferenceEquals(_host, host))
            return;
        DetachTipInternal();
        _host = host;
    }

    /// <summary>关闭当前设置页并摘除宿主引用（页面 Unloaded / 应用退出时调用）。</summary>
    public void Close()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _ = _dispatcher.TryEnqueue(Close);
            return;
        }
        DetachTipInternal();
        _host = null;
    }

    /// <summary>
    /// 关闭当前设置页但不摘除宿主引用——用于页面仍存活、仅需关掉设置页的
    /// 路径（如插件页关闭某个插件）。若用 <see cref="Close"/> 会把 <c>_host</c>
    /// 置空，导致本页其余「···」设置按钮此后全部静默失效。
    /// </summary>
    public void Dismiss()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _ = _dispatcher.TryEnqueue(Dismiss);
            return;
        }
        BeginClose();
    }

    /// <summary>
    /// 打开指定插件的设置页。非 UI 线程调用会先 marshal 到 UI 线程。
    /// 无设置提供方 / 宿主未加载时静默返回。
    /// </summary>
    public void ShowSettings(IPlugin plugin, FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(anchor);
        if (!_dispatcher.HasThreadAccess)
        {
            _ = _dispatcher.TryEnqueue(() => ShowSettings(plugin, anchor));
            return;
        }
        if (_host is not { XamlRoot: not null })
            return;
        if (plugin is not IPluginSettingsProvider provider || provider.Settings.Count == 0)
            return;

        // 关闭上一实例并摘除，避免旧 tip 与新 tip 在树中共存。
        DetachTipInternal();

        // 先恢复持久化值写回插件内存态，再据此构建控件，保证首屏即持久值。
        RestorePersistedValues(provider, plugin.Id);
        var content = BuildSettingsContent(provider, plugin.Id);

        // 全屏透明拦截层：手工承担「点击外部关闭」。不启用内置 light dismiss——
        // 出场动画期间点外部会让其永久失效（WinUI 已知 bug，microsoft-ui-xaml #9143），
        // 且打开期间动态切换 IsLightDismissEnabled 也可能把 tip 卡在屏上。
        var dismissLayer = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            IsHitTestVisible = true
        };
        dismissLayer.Tapped += (_, _) => BeginClose();

        var tip = new TeachingTip
        {
            Target = anchor,
            Title = plugin.DisplayName,
            Content = content,
            // IsLightDismissEnabled 保持默认 false；关闭完全由 _dismissLayer 承担。
            // 从按钮下方出场（用户要求）。
            ShouldConstrainToRootBounds = true,
            PreferredPlacement = TeachingTipPlacementMode.Bottom,
            TailVisibility = TeachingTipTailVisibility.Collapsed,
            MinWidth = TipMinWidth,
            MinHeight = TipMinHeight,
            MaxHeight = TipMaxHeight
        };

        _tip = tip;
        _currentPlugin = plugin;
        _dismissLayer = dismissLayer;
        // 拦截层先入、tip 后入：tip 作为最后 child 处于最高 z-order，
        // 点 tip 内走其自身交互，点 tip 外（拦截层）即关闭。
        _host.Children.Add(dismissLayer);
        _host.Children.Add(tip);
        tip.Closing += OnTipClosing;
        tip.Closed += OnTipClosed;

        // 延迟一帧 + 引用保护打开：挂树后下一个 dispatcher 轮次再开，
        // 期间若被 Close 摘除（ReferenceEquals 不成立）则静默跳过。
        var toOpen = tip;
        _ = _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(_tip, toOpen) && toOpen.XamlRoot is not null)
            {
                _openedAt = Stopwatch.GetTimestamp();
                toOpen.IsOpen = true;
            }
        });
    }

    /// <summary>
    /// 强制清理当前实例：退订事件、关闭并立即从树移除。用于需要立即切换或
    /// 释放的路径（重开 / 换宿主 / 页面 Unloaded / 应用关闭）。不等待退场动画。
    /// </summary>
    private void DetachTipInternal()
    {
        var tip = _tip;
        _tip = null;
        _currentPlugin = null;
        _openedAt = long.MinValue;
        var layer = _dismissLayer;
        _dismissLayer = null;
        if (layer is not null && _host is not null)
        {
            try
            {
                _host.Children.Remove(layer);
            }
            catch
            {
                // 拦截层移除失败仅意味着已被运行时回收，忽略即可。
            }
        }
        if (tip is null)
            return;
        tip.Closing -= OnTipClosing;
        tip.Closed -= OnTipClosed;
        try
        {
            tip.IsOpen = false;
        }
        catch
        {
            // 关闭动画未完成时设置 IsOpen 可能抛出；摘除树后由运行时自行清理。
        }
        if (_host is not null)
        {
            try
            {
                _host.Children.Remove(tip);
            }
            catch
            {
                // 移除失败仅意味着该实例已被运行时回收，忽略即可。
            }
        }
    }

    /// <summary>
    /// 优雅关闭：点拦截层（外部）与 <see cref="Dismiss"/> 共用。入场动画未结束
    /// 前忽略关闭请求（用户要求）；通过 <c>IsOpen = false</c> 触发退场动画，
    /// <see cref="OnTipClosed"/> 在动画结束后移除树元素，保证关闭始终有退场动画。
    /// </summary>
    private void BeginClose()
    {
        var tip = _tip;
        if (tip is null || !CanClose())
            return;
        var layer = _dismissLayer;
        _dismissLayer = null;
        if (layer is not null && _host is not null)
        {
            try
            {
                _host.Children.Remove(layer);
            }
            catch
            {
                // 拦截层移除失败仅意味着已被运行时回收，忽略即可。
            }
        }
        try
        {
            tip.IsOpen = false;
        }
        catch
        {
            // 关闭动画未完成时设置 IsOpen 可能抛出；摘除树后由运行时自行清理。
        }
    }

    /// <summary>入场动画是否已结束、允许发起关闭（无 Opened 事件，用定时近似）。</summary>
    private bool CanClose() =>
        _openedAt != long.MinValue
        && Stopwatch.GetTimestamp() - _openedAt >= OpenAnimationTicks;

    private void OnTipClosing(TeachingTip sender, TeachingTipClosingEventArgs args)
    {
        // 入场动画未结束前禁止任何关闭——含内置 X 关闭按钮（CloseButton reason）。
        if (ReferenceEquals(sender, _tip) && !CanClose())
            args.Cancel = true;
    }

    private void OnTipClosed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (!ReferenceEquals(sender, _tip))
            return;
        // 退场动画已结束：摘除树元素并清空状态。
        _tip = null;
        _currentPlugin = null;
        _openedAt = long.MinValue;
        var layer = _dismissLayer;
        _dismissLayer = null;
        if (layer is not null && _host is not null)
        {
            try
            {
                _host.Children.Remove(layer);
            }
            catch
            {
                // 拦截层移除失败仅意味着已被运行时回收，忽略即可。
            }
        }
        if (_host is not null)
        {
            try
            {
                _host.Children.Remove(sender);
            }
            catch
            {
                // 移除失败仅意味着该实例已被运行时回收，忽略即可。
            }
        }
    }

    // ─── 持久化值恢复 ─────────────────────────────────────────────

    private void RestorePersistedValues(IPluginSettingsProvider provider, string pluginId)
    {
        foreach (var setting in provider.Settings)
        {
            object? value;
            if (_store.TryGetSetting(pluginId, setting.Key, out var stored)
                && TryRestore(setting, stored, out var restored))
            {
                value = restored;
            }
            else
            {
                value = DefaultFor(setting);
            }
            if (value is not null)
                SafeSetProviderValue(provider, setting.Key, value);
        }
    }

    private static object? DefaultFor(IPluginSetting setting) => setting switch
    {
        PluginToggleSetting toggle => toggle.DefaultValue,
        PluginSliderSetting slider => slider.DefaultValue,
        // 空 Options 的 choice 无默认值可取：返回 null，调用方跳过写回。
        // 与 BuildSettingRow 的空 Options 跳过渲染保持一致，避免 RestorePersistedValues
        // 在此处抛 InvalidOperationException 冒泡到未处理的 XAML 事件。
        PluginChoiceSetting choice => choice.Options.Length > 0 ? choice.DefaultValue : null,
        PluginKeyBindingSetting binding =>
            PluginInputBinding.TryParse(
                binding.DefaultValue,
                binding.AllowedKinds,
                out _)
                ? binding.DefaultValue
                : null,
        _ => null
    };

    /// <summary>把存储的 JsonElement 还原为符合描述符类型的 CLR 值；类型不符返回 false。</summary>
    private static bool TryRestore(IPluginSetting setting, JsonElement stored, out object? value)
    {
        switch (setting)
        {
            case PluginToggleSetting:
                if (stored.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = stored.GetBoolean();
                    return true;
                }
                break;
            case PluginSliderSetting slider:
                if (stored.ValueKind == JsonValueKind.Number
                    && stored.TryGetDouble(out var raw))
                {
                    value = CoerceSlider(raw, slider);
                    return true;
                }
                break;
            case PluginChoiceSetting choice:
                if (stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } text
                    && choice.Options.Contains(text, StringComparer.Ordinal))
                {
                    value = text;
                    return true;
                }
                break;
            case PluginKeyBindingSetting binding:
                if (stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } bindingText
                    && PluginInputBinding.TryParse(
                        bindingText,
                        binding.AllowedKinds,
                        out _))
                {
                    value = bindingText;
                    return true;
                }
                break;
        }
        value = null;
        return false;
    }

    private void SafeSetProviderValue(IPluginSettingsProvider provider, string key, object? value)
    {
        try
        {
            provider.SetSettingValue(key, value);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TTM 写回插件设置失败 {key}: {exception}");
        }
    }

    // ─── 内容构建 ─────────────────────────────────────────────────

    private FrameworkElement BuildSettingsContent(
        IPluginSettingsProvider provider, string pluginId)
    {
        var rows = new StackPanel { Spacing = 16, MinWidth = TipMinWidth - 40 };
        foreach (var setting in provider.Settings)
        {
            try
            {
                var row = BuildSettingRow(provider, pluginId, setting);
                if (row is not null)
                    rows.Children.Add(row);
            }
            catch (Exception exception)
            {
                // 单个设置行构建失败不拖垮整个设置页。
                System.Diagnostics.Debug.WriteLine(
                    $"TTM 构建设置行失败 {setting.Key}: {exception}");
            }
        }
        return new ScrollViewer
        {
            Content = rows,
            MaxHeight = ContentMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false
        };
    }

    private FrameworkElement? BuildSettingRow(
        IPluginSettingsProvider provider, string pluginId, IPluginSetting setting)
    {
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = setting.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        });

        switch (setting)
        {
            case PluginToggleSetting toggle:
                {
                    var initial = ReadProviderValue(provider, setting) as bool?
                        ?? toggle.DefaultValue;
                    var control = new ToggleSwitch
                    {
                        OnContent = "开",
                        OffContent = "关",
                        IsOn = initial
                    };
                    control.Toggled += (_, _) =>
                        PersistSetting(provider, pluginId, setting.Key,
                            JsonSerializer.SerializeToElement(control.IsOn));
                    row.Children.Add(control);
                    break;
                }
            case PluginSliderSetting slider:
                {
                    if (slider.Maximum < slider.Minimum
                        || double.IsNaN(slider.Minimum) || double.IsNaN(slider.Maximum))
                    {
                        break;
                    }
                    var initial = ReadProviderValue(provider, setting) switch
                    {
                        double d => d,
                        long l => (double)l,
                        int i => (double)i,
                        _ => slider.DefaultValue
                    };
                    initial = CoerceSlider(initial, slider);
                    var valueText = new TextBlock
                    {
                        Text = FormatSliderValue(initial),
                        MinWidth = 48,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                    };
                    var control = new Slider
                    {
                        Minimum = slider.Minimum,
                        Maximum = slider.Maximum,
                        StepFrequency = Math.Max(0, Math.Min(
                            slider.StepFrequency, slider.Maximum - slider.Minimum)),
                        SnapsTo = SliderSnapsTo.StepValues,
                        Value = initial
                    };
                    var sliderRow = new Grid { ColumnSpacing = 12 };
                    sliderRow.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                    sliderRow.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });
                    Grid.SetColumn(control, 0);
                    Grid.SetColumn(valueText, 1);
                    sliderRow.Children.Add(control);
                    sliderRow.Children.Add(valueText);
                    control.ValueChanged += (_, e) =>
                    {
                        var snapped = SnapSliderValue(e.NewValue, slider);
                        valueText.Text = FormatSliderValue(snapped);
                        PersistSetting(provider, pluginId, setting.Key,
                            JsonSerializer.SerializeToElement(snapped));
                    };
                    row.Children.Add(sliderRow);
                    break;
                }
            case PluginChoiceSetting choice:
                {
                    if (choice.Options.Length == 0)
                        break;
                    var raw = ReadProviderValue(provider, setting);
                    var selectedIndex = raw is string s
                        && Array.IndexOf(choice.Options, s) >= 0
                            ? Array.IndexOf(choice.Options, s)
                            : choice.DefaultIndex;
                    selectedIndex = Math.Clamp(
                        selectedIndex, 0, choice.Options.Length - 1);
                    var control = new ComboBox
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        MinWidth = 200
                    };
                    foreach (var option in choice.Options)
                        control.Items.Add(option);
                    control.SelectedIndex = selectedIndex;
                    control.SelectionChanged += (_, _) =>
                    {
                        if (control.SelectedIndex >= 0
                            && control.SelectedIndex < choice.Options.Length)
                        {
                            PersistSetting(provider, pluginId, setting.Key,
                                JsonSerializer.SerializeToElement(
                                    choice.Options[control.SelectedIndex]));
                        }
                    };
                    row.Children.Add(control);
                    break;
                }
            case PluginKeyBindingSetting binding:
                row.Children.Add(BuildKeyBindingControl(
                    provider,
                    pluginId,
                    binding));
                break;
            default:
                row.Children.Add(new TextBlock
                {
                    Text = $"不支持的设置类型：{setting.GetType().Name}",
                    FontSize = 12,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
                });
                break;
        }

        if (!string.IsNullOrWhiteSpace(setting.Description))
        {
            row.Children.Add(new TextBlock
            {
                Text = setting.Description,
                FontSize = 12,
                Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        return row;
    }

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
            if (recording)
                return;
            if (binding.IsConfigured)
            {
                _ = SaveBinding(new PluginInputBinding());
                return;
            }

            recording = true;
            modifiers = PluginInputModifiers.None;
            RefreshButton();
            host.Focus(FocusState.Programmatic);
        };

        host.KeyDown += async (_, args) =>
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
                modifiers);
            if ((setting.AllowedKinds & PluginInputBindingKinds.Keyboard) != 0)
                await SaveBinding(next);
        };

        host.KeyUp += async (_, args) =>
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
        };

        host.PointerPressed += async (_, args) =>
        {
            if (!recording
                || (setting.AllowedKinds & PluginInputBindingKinds.Mouse) == 0)
            {
                return;
            }

            var properties = args.GetCurrentPoint(host).Properties;
            var mouseButton = properties.IsLeftButtonPressed
                ? PluginMouseButton.Left
                : properties.IsRightButtonPressed
                    ? PluginMouseButton.Right
                    : properties.IsMiddleButtonPressed
                        ? PluginMouseButton.Middle
                        : properties.IsXButton1Pressed
                            ? PluginMouseButton.XButton1
                            : PluginMouseButton.XButton2;
            args.Handled = true;
            await SaveBinding(PluginInputBinding.Mouse(mouseButton));
        };

        RefreshButton();
        return control;
    }

    private static bool TryGetPluginModifier(
        Windows.System.VirtualKey key,
        out PluginInputModifiers modifier)
    {
        modifier = (uint)key switch
        {
            0x10 or 0xA0 or 0xA1 => PluginInputModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => PluginInputModifiers.Control,
            0x12 or 0xA4 or 0xA5 => PluginInputModifiers.Alt,
            0x5B or 0x5C => PluginInputModifiers.Windows,
            _ => PluginInputModifiers.None
        };
        return modifier != PluginInputModifiers.None;
    }

    private static object? ReadProviderValue(
        IPluginSettingsProvider provider, IPluginSetting setting)
    {
        try
        {
            return provider.GetSettingValue(setting.Key);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TTM 读取设置值失败 {setting.Key}: {exception}");
            return null;
        }
    }

    // ─── 持久化 ───────────────────────────────────────────────────

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

    // ─── 数值辅助 ─────────────────────────────────────────────────

    private static double CoerceSlider(double value, PluginSliderSetting slider)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = slider.DefaultValue;
        return Math.Clamp(value, slider.Minimum, slider.Maximum);
    }

    private static double SnapSliderValue(double value, PluginSliderSetting slider)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = slider.Minimum;
        if (slider.StepFrequency > 0)
            value = Math.Round(value / slider.StepFrequency) * slider.StepFrequency;
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        // 消除二进制浮点误差：5.999999999 之类的抖动值归整。
        return Math.Round(value, 3);
    }

    private static string FormatSliderValue(double value) =>
        value == Math.Floor(value) ? value.ToString("0") : value.ToString("0.###");
}
