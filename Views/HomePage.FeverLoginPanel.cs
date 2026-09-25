using System.Diagnostics;
using IDVBuff.Features.GameLaunch;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
    private TeachingTip? _feverLoginTip;

    private async Task ShowFeverAccountsAsync(Panel host, Button anchor)
    {
        if (_feverLoginTip is not null)
        {
            _feverLoginTip.IsOpen = false;
            return;
        }

        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 8),
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
        };
        var body = new StackPanel { Spacing = 8, Width = 280 };

        var titleBlock = new TextBlock
        {
            Text = "网易官服账号管理",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var back = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Content = new FontIcon { Glyph = "\uE72B", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTipService.SetToolTip(back, "关闭面板");

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        Grid.SetColumn(back, 0);
        Grid.SetColumn(titleBlock, 1);
        header.Children.Add(back);
        header.Children.Add(titleBlock);

        var content = new StackPanel
        {
            Spacing = 8,
            Width = 280,
            Padding = new Thickness(0, 0, 0, 4),
            Children = { header, body }
        };

        var tip = new TeachingTip
        {
            Target = anchor,
            IsLightDismissEnabled = true,
            PreferredPlacement = TeachingTipPlacementMode.Bottom,
            Content = content
        };
        _feverLoginTip = tip;
        back.Click += (_, _) => tip.IsOpen = false;
        tip.Closed += (_, _) =>
        {
            host.Children.Remove(tip);
            if (_feverLoginTip == tip) _feverLoginTip = null;
        };

        host.Children.Add(tip);
        FeverAccountStore.Instance.AutoImportExistingSessionIfEmpty();
        AttachFeverAccounts(tip, body, message);

        _ = tip.DispatcherQueue.TryEnqueue(() =>
        {
            if (_feverLoginTip == tip)
            {
                tip.IsOpen = true;
            }
        });
    }

    private void AttachFeverAccounts(TeachingTip tip, StackPanel container, TextBlock message)
    {
        container.Children.Clear();

        var list = new StackPanel { Spacing = 6 };
        var viewport = new ScrollViewer
        {
            Content = list,
            MaxHeight = 130,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed
        };

        var addLongTermBtn = new Button
        {
            Content = "网易账号登陆",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var addTempBtn = new Button
        {
            Content = "临时扫码登陆",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0)
        };

        container.Children.Add(viewport);
        container.Children.Add(addLongTermBtn);
        container.Children.Add(addTempBtn);
        container.Children.Add(message);

        void RenderAccounts()
        {
            list.Children.Clear();
            var meta = FeverAccountStore.Instance.LoadMetadata();
            var accounts = meta.Accounts;

            if (accounts.Count == 0)
            {
                viewport.Visibility = Visibility.Collapsed;
                message.Text = "暂无保存的官服账号。点击上方按钮登录添加。";
                return;
            }

            viewport.Visibility = Visibility.Visible;
            message.Text = "点击账号即可切换。点击主界面“启动游戏”进入。";

            foreach (var acc in accounts)
            {
                var isCurrent = string.Equals(acc.Id, meta.ActiveAccountId, StringComparison.OrdinalIgnoreCase);

                var rowGrid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Margin = new Thickness(0, 1, 0, 1)
                };

                var caption = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (isCurrent)
                {
                    caption.Inlines.Add(new Run
                    {
                        Text = "✓ ",
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush
                    });
                }

                var badgeText = acc.IsLongTerm ? "[网易] " : "[临时] ";
                var badgeBrush = acc.IsLongTerm
                    ? Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush
                    : Application.Current.Resources["TextFillColorTertiaryBrush"] as Brush;

                caption.Inlines.Add(new Run
                {
                    Text = badgeText,
                    FontSize = 11,
                    Foreground = badgeBrush
                });

                caption.Inlines.Add(new Run
                {
                    Text = acc.Name,
                    FontWeight = isCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
                });

                var selectBtn = new Button
                {
                    Content = caption,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };

                selectBtn.Click += async (_, _) =>
                {
                    selectBtn.IsEnabled = false;
                    message.Text = $"正在切换至 [{acc.Name}]…";
                    try
                    {
                        var ok = await FeverAccountStore.Instance.SwitchAccountAsync(acc.Id);
                        if (ok)
                        {
                            var ticket = FeverAccountStore.Instance.GetAccountTicket(acc.Id);
                            FeverIpcBridge.Instance.SetTicket(ticket);
                            RenderAccounts();
                            message.Text = acc.IsLongTerm
                                ? $"已切换为网易账号 [{acc.Name}]。点击“启动游戏”即可进入。"
                                : $"已切换为临时账号 [{acc.Name}]。点击“启动游戏”即可进入。";
                        }
                        else
                        {
                            message.Text = "切换失败，未找到该账号的凭据备份。";
                        }
                    }
                    catch (Exception ex)
                    {
                        message.Text = $"切换异常：{ex.Message}";
                    }
                    finally
                    {
                        selectBtn.IsEnabled = true;
                    }
                };

                var menu = new MenuFlyout();
                var launchItem = new MenuFlyoutItem
                {
                    Text = "启动此账号",
                    Icon = new SymbolIcon(Symbol.Play)
                };
                var renameItem = new MenuFlyoutItem
                {
                    Text = "重命名",
                    Icon = new SymbolIcon(Symbol.Rename)
                };
                var deleteItem = new MenuFlyoutItem
                {
                    Text = "删除账号",
                    Icon = new SymbolIcon(Symbol.Delete)
                };

                launchItem.Click += async (_, _) =>
                {
                    tip.IsOpen = false;
                    try
                    {
                        await FeverAccountStore.Instance.SwitchAccountAsync(acc.Id);
                        var ticket = FeverAccountStore.Instance.GetAccountTicket(acc.Id);
                        FeverIpcBridge.Instance.SetTicket(ticket);
                        await LaunchOfficialGameAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HomePage] 启动此账号异常: {ex.Message}");
                    }
                };

                renameItem.Click += async (_, _) =>
                {
                    var input = new TextBox { Text = acc.Name, SelectionStart = acc.Name.Length };
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "重命名账号",
                        Content = input,
                        PrimaryButtonText = "保存",
                        CloseButtonText = "取消"
                    };
                    var res = await dialog.ShowThemedAsync();
                    if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
                    {
                        FeverAccountStore.Instance.RenameAccount(acc.Id, input.Text.Trim());
                        RenderAccounts();
                    }
                };

                deleteItem.Click += async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "删除官服账号",
                        Content = $"确定要删除账号档案 [{acc.Name}] 吗？\n删除后如需再次使用该账号，需要重新登录。",
                        PrimaryButtonText = "确认删除",
                        CloseButtonText = "取消"
                    };
                    var res = await dialog.ShowThemedAsync();
                    if (res == ContentDialogResult.Primary)
                    {
                        FeverAccountStore.Instance.DeleteAccount(acc.Id);
                        RenderAccounts();
                    }
                };

                menu.Items.Add(launchItem);
                menu.Items.Add(renameItem);
                menu.Items.Add(deleteItem);

                var moreBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE712", FontSize = 12 },
                    Flyout = menu,
                    Width = 32,
                    Height = 32,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Margin = new Thickness(4, 0, 0, 0),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0)
                };
                ToolTipService.SetToolTip(moreBtn, "更多操作");

                Grid.SetColumn(selectBtn, 0);
                Grid.SetColumn(moreBtn, 1);
                rowGrid.Children.Add(selectBtn);
                rowGrid.Children.Add(moreBtn);

                list.Children.Add(rowGrid);
            }
        }

        async Task PerformLongTermLoginAsync()
        {
            tip.IsLightDismissEnabled = false;
            addLongTermBtn.IsEnabled = false;
            addTempBtn.IsEnabled = false;
            try
            {
                var (currentToken, currentSdkuid) = FeverAccountStore.ReadFeverRegistryCredentials();

                using var protocolKey = Registry.ClassesRoot.OpenSubKey(@"fevergames\shell\open\command", writable: false);
                if (!FeverGamesLaunchPlan.TryCreate(protocolKey?.GetValue(null) as string, File.Exists, out var plan, out var err))
                {
                    message.Text = $"未找到发烧游戏启动器：{err}";
                    return;
                }

                FeverAccountStore.ClearFeverRegistryCredentials();
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = plan.LauncherPath,
                        WorkingDirectory = Path.GetDirectoryName(plan.LauncherPath),
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    FeverAccountStore.WriteFeverRegistryCredentials(currentToken, currentSdkuid);
                    message.Text = $"呼出发烧平台失败：{ex.Message}";
                    return;
                }

                message.Text = "已呼出网易发烧平台，请在平台中登录新账号，完成后将自动同步…";

                var initialToken = currentToken;
                var captured = false;
                for (int i = 0; i < 90; i++)
                {
                    await Task.Delay(1000);
                    var (tokenNow, _) = FeverAccountStore.ReadFeverRegistryCredentials();
                    if (!string.IsNullOrWhiteSpace(tokenNow) && !string.Equals(tokenNow, initialToken, StringComparison.Ordinal))
                    {
                        var profile = FeverAccountStore.Instance.CaptureCurrentFeverRegistryAccount();
                        RenderAccounts();
                        message.Text = $"新网易账号 [{profile?.Name}] 已成功同步并保存！";
                        captured = true;
                        break;
                    }
                }

                if (!captured)
                {
                    message.Text = "若已在发烧平台完成登录，可再次点击“网易账号登陆”同步凭据。";
                }
            }
            catch (Exception ex)
            {
                message.Text = $"操作异常：{ex.Message}";
            }
            finally
            {
                tip.IsLightDismissEnabled = true;
                addLongTermBtn.IsEnabled = true;
                addTempBtn.IsEnabled = true;
            }
        }

        async Task PerformTempLoginAsync()
        {
            tip.IsLightDismissEnabled = false;
            addLongTermBtn.IsEnabled = false;
            addTempBtn.IsEnabled = false;
            message.Text = "正在准备全新临时登录环境…";

            nint hwnd = 0;
            try
            {
                if (Application.Current is App currentApp && currentApp.MainWindow is not null)
                    hwnd = WinRT.Interop.WindowNative.GetWindowHandle(currentApp.MainWindow);
            }
            catch { }

            FeverAccountStore.Instance.PrepareForNewLogin();

            try
            {
                message.Text = "正在呼出临时扫码窗口（请使用第五人格手游扫码器）…";
                var result = await FeverLoginService.StartLoginAsync(hwnd, isLongTerm: false);
                if (!result.Success)
                {
                    await FeverAccountStore.Instance.RollbackNewLoginAsync();
                    message.Text = result.Message ?? "扫码登录未完成。";
                    return;
                }

                var ticket = result.Ticket;
                if (string.IsNullOrWhiteSpace(ticket))
                {
                    message.Text = "扫码完成，但未取得有效登录凭据。请重新扫码。";
                    await FeverAccountStore.Instance.RollbackNewLoginAsync();
                    return;
                }

                message.Text = "登录成功，正在保存临时凭据…";
                var newProfile = await FeverAccountStore.Instance.CaptureCurrentAccountAsync(
                    ticket: ticket,
                    isLongTerm: false);

                if (!string.IsNullOrWhiteSpace(ticket))
                {
                    FeverIpcBridge.Instance.SetTicket(ticket.Trim());
                }
                RenderAccounts();
                message.Text = $"临时账号 [{newProfile.Name}] 已就绪！点击“启动游戏”即可进入。";
            }
            catch (Exception ex)
            {
                await FeverAccountStore.Instance.RollbackNewLoginAsync();
                message.Text = $"保存账号失败：{ex.Message}";
            }
            finally
            {
                tip.IsLightDismissEnabled = true;
                addLongTermBtn.IsEnabled = true;
                addTempBtn.IsEnabled = true;
            }
        }

        addLongTermBtn.Click += async (_, _) => await PerformLongTermLoginAsync();
        addTempBtn.Click += async (_, _) => await PerformTempLoginAsync();

        RenderAccounts();
    }
}
