using System.Text.Json;
using IDVBuff.Features.Plugins;
using IDVBuff.Features.Plugins.V2;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class PluginsPage
{
    private async Task ShowThirdPartySettingsAsync(string pluginId)
    {
        if (App.ThirdPartyPlugins is not { } runtime) return;
        try
        {
            var (manifest, settings) = await runtime.GetSettingsAsync(pluginId);
            var panel = new StackPanel
            {
                Spacing = 14,
                MinWidth = 420,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
            };
            var readers = new Dictionary<string, Func<JsonElement>>(StringComparer.Ordinal);
            var numericEditors = new List<(NumberBox Input, Func<Task> Commit)>();
            foreach (var definition in manifest.Settings)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = definition.DisplayName,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                var value = settings.Current.TryGetValue(definition.Key, out var stored)
                    ? stored
                    : definition.Default;
                switch (definition.Type)
                {
                    case "toggle":
                        var toggle = new ToggleSwitch { IsOn = value.GetBoolean() };
                        panel.Children.Add(toggle);
                        readers[definition.Key] = () => JsonSerializer.SerializeToElement(toggle.IsOn);
                        break;
                    case "slider":
                        var slider = new Slider
                        {
                            Minimum = definition.Minimum!.Value,
                            Maximum = definition.Maximum!.Value,
                            StepFrequency = definition.Step!.Value,
                            SnapsTo = SliderSnapsTo.StepValues,
                            Value = value.GetDouble()
                        };
                        var numberInput = new NumberBox
                        {
                            Minimum = slider.Minimum,
                            Maximum = slider.Maximum,
                            SmallChange = slider.StepFrequency,
                            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                            Value = slider.Value,
                            Width = 88,
                            VerticalAlignment = VerticalAlignment.Center
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
                        Grid.SetColumn(slider, 0);
                        Grid.SetColumn(numberInput, 1);
                        sliderRow.Children.Add(slider);
                        sliderRow.Children.Add(numberInput);
                        slider.ValueChanged += (_, args) => numberInput.Value = args.NewValue;
                        async Task CommitNumberInputAsync()
                        {
                            var requested = PluginNumericInput.TryGetValue(numberInput, out var typedValue)
                                ? typedValue
                                : double.IsFinite(numberInput.Value)
                                    ? numberInput.Value
                                : slider.Value;
                            var snapped = SnapThirdPartySliderValue(
                                requested,
                                slider.Minimum,
                                slider.Maximum,
                                slider.StepFrequency);
                            numberInput.Value = snapped;
                            slider.Value = snapped;
                            try
                            {
                                await settings.UpdateAsync(
                                    definition.Key,
                                    JsonSerializer.SerializeToElement(snapped));
                            }
                            catch (Exception exception)
                            {
                                ShowThirdPartyNotice(
                                    InfoBarSeverity.Error,
                                    "插件设置保存失败",
                                    exception.Message);
                            }
                        }
                        numberInput.LostFocus += async (_, _) => await CommitNumberInputAsync();
                        numericEditors.Add((numberInput, CommitNumberInputAsync));
                        var textSaveQueue = Task.CompletedTask;
                        PluginNumericInput.Attach(numberInput, typedValue =>
                        {
                            var snapped = SnapThirdPartySliderValue(
                                typedValue,
                                slider.Minimum,
                                slider.Maximum,
                                slider.StepFrequency);
                            textSaveQueue = SaveTypedSliderValueAsync(textSaveQueue, snapped);
                        }, () => _ = CommitNumberInputAsync());

                        async Task SaveTypedSliderValueAsync(Task previousSave, double typedValue)
                        {
                            try
                            {
                                await previousSave;
                                await settings.UpdateAsync(
                                    definition.Key,
                                    JsonSerializer.SerializeToElement(typedValue));
                            }
                            catch (Exception exception)
                            {
                                ShowThirdPartyNotice(
                                    InfoBarSeverity.Error,
                                    "插件设置保存失败",
                                    exception.Message);
                            }
                        }
                        panel.Children.Add(sliderRow);
                        readers[definition.Key] = () => JsonSerializer.SerializeToElement(slider.Value);
                        break;
                    case "choice":
                        var choice = new ComboBox
                        {
                            ItemsSource = definition.Options.Select(option => option.Value).ToArray(),
                            SelectedItem = value.GetString(),
                            MinWidth = 240
                        };
                        panel.Children.Add(choice);
                        readers[definition.Key] = () => JsonSerializer.SerializeToElement(
                            choice.SelectedItem as string ?? definition.Options[0].Value);
                        break;
                    case "keyBinding":
                        var binding = new TextBox
                        {
                            Text = value.ValueKind == JsonValueKind.String ? value.GetString() : "none",
                            PlaceholderText = "keyboard:70:0 / mouse:0 / none"
                        };
                        panel.Children.Add(binding);
                        readers[definition.Key] = () => JsonSerializer.SerializeToElement(binding.Text);
                        break;
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"{manifest.DisplayName} 设置",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                Content = new ScrollViewer
                {
                    MaxHeight = 520,
                    Content = panel,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                }
            };
            dialog.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(async (_, args) =>
            {
                if (args.OriginalSource is not DependencyObject source) return;
                foreach (var editor in numericEditors)
                {
                    var focused = FocusManager.GetFocusedElement(editor.Input.XamlRoot) as DependencyObject;
                    if (focused is not null
                        && IsWithinThirdPartyElement(focused, editor.Input)
                        && !IsWithinThirdPartyElement(source, editor.Input))
                        await editor.Commit();
                }
            }), handledEventsToo: true);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            foreach (var (key, read) in readers)
                await settings.UpdateAsync(key, read());
            ShowThirdPartyNotice(InfoBarSeverity.Success, "设置已保存", manifest.DisplayName);
        }
        catch (Exception exception)
        {
            ShowThirdPartyNotice(InfoBarSeverity.Error, "插件设置保存失败", exception.Message);
        }
    }

    private static double SnapThirdPartySliderValue(
        double value,
        double minimum,
        double maximum,
        double step)
    {
        if (!double.IsFinite(value)) value = minimum;
        if (step > 0)
            value = minimum + Math.Round((value - minimum) / step) * step;
        return Math.Round(Math.Clamp(value, minimum, maximum), 3);
    }

    private static bool IsWithinThirdPartyElement(
        DependencyObject source,
        DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private async Task ExecuteThirdPartyCommandAsync(string pluginId, string commandId, Button button)
    {
        if (App.ThirdPartyPlugins is not { } runtime) return;
        button.IsEnabled = false;
        try
        {
            var result = await runtime.ExecuteCommandAsync(pluginId, commandId);
            ShowThirdPartyNotice(
                result.Status == PluginCommandStatus.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                result.Status == PluginCommandStatus.Success ? "命令已完成" : "命令未完成",
                result.Message ?? commandId);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task UninstallThirdPartyPluginAsync(PluginCatalogEntry entry, Button button)
    {
        if (App.ThirdPartyPluginInstaller is not { } installer ||
            App.ThirdPartyPlugins is not { } runtime)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"卸载 {entry.DisplayName}？",
            Content = "插件包将在下次启动时删除；插件私有数据默认保留。",
            PrimaryButtonText = "卸载并保留数据",
            SecondaryButtonText = "卸载并删除数据",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        button.IsEnabled = false;
        try
        {
            await runtime.SetEnabledAsync(entry.Id, false);
            await installer.MarkForUninstallAsync(
                entry.Id,
                deleteData: result == ContentDialogResult.Secondary);
            await RefreshThirdPartyPluginsAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void PluginNotifications_NotificationPosted(object? sender, HostedPluginNotification notification)
    {
        DispatcherQueue.TryEnqueue(() => ShowThirdPartyNotice(
            notification.Notification.Severity switch
            {
                PluginNotificationSeverity.Success => InfoBarSeverity.Success,
                PluginNotificationSeverity.Warning => InfoBarSeverity.Warning,
                PluginNotificationSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational
            },
            notification.Notification.Title,
            notification.Notification.Message));
    }

    private void ShowThirdPartyNotice(InfoBarSeverity severity, string title, string message)
    {
        if (_thirdPartyNotice is null) return;
        _thirdPartyNotice.Severity = severity;
        _thirdPartyNotice.Title = title;
        _thirdPartyNotice.Message = message;
        _thirdPartyNotice.IsOpen = true;
    }

    private static string BuildPluginStateText(PluginCatalogEntry entry)
    {
        if (entry.PendingDelete)
            return entry.DeleteDataOnUninstall
                ? "等待重启后卸载；插件私有数据也将删除。"
                : "等待重启后卸载；插件私有数据将保留。";
        if (entry.PendingVersion is not null) return "新版本已验证，等待重启激活；当前保持禁用。";
        if (entry.CapabilityApprovalRequired) return "请求能力发生扩张，需要重新批准。";
        if (entry.QuarantineReason is not null) return $"已隔离：{entry.QuarantineReason}";
        return entry.Enabled ? "已启用" : "已安装，默认禁用";
    }
}
