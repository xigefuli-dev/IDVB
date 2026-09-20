using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class PluginsPage
{
    private void AddThirdPartySection(StackPanel content)
    {
        content.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 0),
            Background = FluentTheme.Brush("DividerStrokeColorDefaultBrush")
        });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { Spacing = 6 };
        copy.Children.Add(new TextBlock
        {
            Text = "第三方插件",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        copy.Children.Add(new TextBlock
        {
            Text = "IDVP 是受信任的主进程扩展，不是安全沙箱。安装后默认禁用。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryTextBrush
        });
        header.Children.Add(copy);
        var importButton = new Button
        {
            Content = "导入 .idvp",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        importButton.Click += ImportThirdPartyPlugin_Click;
        Grid.SetColumn(importButton, 1);
        header.Children.Add(importButton);
        content.Children.Add(header);

        if (App.ThirdPartyPluginDirectories?.DeveloperMode == true)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "插件开发者模式",
                Message = "允许未签名 IDVP；包、信任和启用状态与正式模式完全隔离。"
            });
        }

        if (App.ThirdPartyPlugins?.SafeMode.IsActive == true)
        {
            var disableAllButton = new Button { Content = "禁用全部第三方插件" };
            disableAllButton.Click += async (_, _) =>
            {
                if (App.ThirdPartyPlugins is not { } runtime) return;
                disableAllButton.IsEnabled = false;
                try
                {
                    await runtime.DisableAllAsync();
                    await RefreshThirdPartyPluginsAsync();
                    ShowThirdPartyNotice(
                        InfoBarSeverity.Success,
                        "已禁用全部第三方插件",
                        "重启 IDVB 后将退出本次安全模式。插件包和私有数据均未删除。");
                }
                catch (Exception exception)
                {
                    ShowThirdPartyNotice(InfoBarSeverity.Error, "无法禁用第三方插件", exception.Message);
                }
                finally
                {
                    disableAllButton.IsEnabled = true;
                }
            };
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "第三方插件安全模式",
                Message = "检测到连续异常退出，本次启动未加载任何第三方插件。",
                ActionButton = disableAllButton
            });
        }

        _thirdPartyNotice = new InfoBar { IsOpen = false, IsClosable = true };
        content.Children.Add(_thirdPartyNotice);
        _thirdPartyContainer = new StackPanel { Spacing = 12 };
        content.Children.Add(_thirdPartyContainer);
    }

    private async void ImportThirdPartyPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || App.ThirdPartyPluginInstaller is not { } installer)
            return;
        button.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                CommitButtonText = "检查并导入",
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".idvp");
            var file = await picker.PickSingleFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                return;

            var package = await installer.InspectAsync(file.Path);
            if (!await ConfirmInstallAsync(package))
                return;
            var result = await installer.InstallAsync(
                file.Path,
                new PluginInstallApproval
                {
                    TrustPublisher = true,
                    ApprovedCapabilities = package.Manifest.Capabilities.ToHashSet(StringComparer.Ordinal)
                });
            if (App.ThirdPartyPlugins is { } runtime)
                await runtime.SetEnabledAsync(result.CatalogEntry.Id, false);
            ShowThirdPartyNotice(
                InfoBarSeverity.Success,
                "插件已安装但未启用",
                $"{result.CatalogEntry.DisplayName} {result.InstalledVersion} 将在下次启动后可启用。");
            await RefreshThirdPartyPluginsAsync();
        }
        catch (Exception exception)
        {
            ShowThirdPartyNotice(InfoBarSeverity.Error, "IDVP 导入失败", exception.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmInstallAsync(IdvpValidatedPackage package)
    {
        var manifest = package.Manifest;
        var capabilities = manifest.Capabilities.Count == 0
            ? "无宿主能力"
            : string.Join(Environment.NewLine, manifest.Capabilities.Select(capability => $"• {capability}"));
        var risks = new List<string>();
        if (manifest.Risks.NativeCode) risks.Add("包含原生代码");
        if (manifest.Risks.NetworkAccess) risks.Add("可能访问网络");
        if (manifest.Risks.ExternalFileAccess) risks.Add("可能访问插件目录外文件");
        if (manifest.Risks.InputAutomation) risks.Add("包含输入自动化行为");
        if (risks.Count == 0) risks.Add("未声明额外高风险行为");
        var keyText = package.IsSigned
            ? package.Signature.KeyId
            : "未签名（仅开发者模式）";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"信任并安装 {manifest.DisplayName}？",
            PrimaryButtonText = "信任、批准能力并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock
                {
                    Text = $"发布者：{manifest.Publisher.Name} ({manifest.Publisher.Id})\n" +
                           $"密钥指纹：{keyText}\n版本：{manifest.Version}\n\n" +
                           $"请求能力：\n{capabilities}\n\n风险声明：\n{string.Join(Environment.NewLine, risks.Select(risk => $"• {risk}"))}\n\n" +
                           "插件将在 IDVB 主进程内运行；这些能力只限制宿主 API，不构成系统级沙箱。",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RefreshThirdPartyPluginsAsync()
    {
        if (_thirdPartyContainer is null || App.ThirdPartyPluginState is not { } state)
            return;
        try
        {
            var catalog = await state.ReadCatalogAsync();
            _thirdPartyContainer.Children.Clear();
            if (catalog.Plugins.Count == 0)
            {
                _thirdPartyContainer.Children.Add(new TextBlock
                {
                    Text = "尚未安装第三方插件。",
                    Foreground = SecondaryTextBrush
                });
                return;
            }

            foreach (var plugin in catalog.Plugins.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
                _thirdPartyContainer.Children.Add(await CreateThirdPartyPluginCardAsync(plugin));
        }
        catch (Exception exception)
        {
            ShowThirdPartyNotice(InfoBarSeverity.Error, "无法读取第三方插件目录", exception.Message);
        }
    }

    private async Task<Border> CreateThirdPartyPluginCardAsync(PluginCatalogEntry entry)
    {
        var runtime = App.ThirdPartyPlugins;
        IdvpManifest? manifest = null;
        try
        {
            if (runtime is not null)
                manifest = (await runtime.GetSettingsAsync(entry.Id)).Manifest;
        }
        catch
        {
        }

        var root = new Grid { ColumnSpacing = 14 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(new TextBlock
        {
            Text = $"{entry.DisplayName}  v{entry.PendingVersion ?? entry.ActiveVersion}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        body.Children.Add(new TextBlock
        {
            Text = $"发布者：{entry.PublisherName} · ID: {entry.Id}",
            FontSize = 12,
            Foreground = SecondaryTextBrush
        });
        body.Children.Add(new TextBlock
        {
            Text = BuildPluginStateText(entry),
            FontSize = 12,
            Foreground = entry.QuarantineReason is null ? SecondaryTextBrush : FluentTheme.Brush("SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(body);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (manifest is not null)
        {
            foreach (var command in manifest.Commands)
            {
                var commandButton = new Button
                {
                    Content = command.DisplayName,
                    IsEnabled = entry.Enabled && !entry.PendingDelete
                        && runtime?.IsMatchActivationAllowed == true
                };
                commandButton.Click += async (_, _) => await ExecuteThirdPartyCommandAsync(entry.Id, command.Id, commandButton);
                actions.Children.Add(commandButton);
            }

            if (manifest.Settings.Count > 0)
            {
                var settingsButton = new Button { Content = "设置", IsEnabled = !entry.PendingDelete };
                settingsButton.Click += async (_, _) => await ShowThirdPartySettingsAsync(entry.Id);
                actions.Children.Add(settingsButton);
            }
        }

        if (entry.QuarantineReason is not null)
        {
            var retryButton = new Button { Content = "清除隔离并重试" };
            retryButton.Click += async (_, _) =>
            {
                if (App.ThirdPartyPlugins is not { } manager) return;
                retryButton.IsEnabled = false;
                try
                {
                    await manager.RetryAsync(entry.Id);
                    await RefreshThirdPartyPluginsAsync();
                }
                catch (Exception exception)
                {
                    ShowThirdPartyNotice(InfoBarSeverity.Error, "插件重试失败", exception.Message);
                }
                finally
                {
                    retryButton.IsEnabled = true;
                }
            };
            actions.Children.Add(retryButton);
        }

        if (entry.PreviousVersions.Count > 0 && entry.PendingVersion is null && !entry.PendingDelete)
        {
            var rollbackButton = new Button { Content = "回滚" };
            rollbackButton.Click += async (_, _) =>
            {
                if (App.ThirdPartyPlugins is not { } manager) return;
                rollbackButton.IsEnabled = false;
                try
                {
                    await manager.ScheduleRollbackAsync(entry.Id);
                    await RefreshThirdPartyPluginsAsync();
                    ShowThirdPartyNotice(
                        InfoBarSeverity.Success,
                        "已安排插件回滚",
                        $"{entry.DisplayName} 将在下次启动时切换到上一保留版本。");
                }
                catch (Exception exception)
                {
                    ShowThirdPartyNotice(InfoBarSeverity.Error, "无法回滚插件", exception.Message);
                }
                finally
                {
                    rollbackButton.IsEnabled = true;
                }
            };
            actions.Children.Add(rollbackButton);
        }

        var uninstallButton = new Button { Content = "卸载" };
        uninstallButton.Click += async (_, _) => await UninstallThirdPartyPluginAsync(entry, uninstallButton);
        actions.Children.Add(uninstallButton);
        var toggle = new ToggleSwitch
        {
            IsOn = entry.Enabled,
            IsEnabled = entry.ActiveVersion is not null && entry.PendingVersion is null &&
                        !entry.CapabilityApprovalRequired && entry.QuarantineReason is null &&
                        !entry.PendingDelete,
            OffContent = "禁用",
            OnContent = "启用"
        };
        var changing = false;
        toggle.Toggled += async (_, _) =>
        {
            if (changing || App.ThirdPartyPlugins is not { } manager) return;
            changing = true;
            toggle.IsEnabled = false;
            try
            {
                await manager.SetEnabledAsync(entry.Id, toggle.IsOn);
                await RefreshThirdPartyPluginsAsync();
            }
            catch (Exception exception)
            {
                toggle.IsOn = entry.Enabled;
                ShowThirdPartyNotice(InfoBarSeverity.Error, "无法更改插件状态", exception.Message);
            }
            finally
            {
                changing = false;
                toggle.IsEnabled = true;
            }
        };
        actions.Children.Add(toggle);
        Grid.SetColumn(actions, 1);
        root.Children.Add(actions);
        return new Border
        {
            Padding = new Thickness(18),
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = root
        };
    }

}
