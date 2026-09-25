using IDVBuff.Features.GameLaunch;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

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
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
        };
        var body = new StackPanel { Spacing = 10, Width = 280 };

        var titleBlock = new TextBlock
        {
            Text = "网易官服账号",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4)
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
        header.Children.Add(back);
        header.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { titleBlock }
        });

        var content = new StackPanel
        {
            Spacing = 8,
            Width = 280,
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
        AttachFeverAccounts(tip, body, message);
        tip.IsOpen = true;
    }

    private void AttachFeverAccounts(TeachingTip tip, StackPanel container, TextBlock message)
    {
        container.Children.Clear();

        var list = new StackPanel { Spacing = 6 };
        var viewport = new ScrollViewer
        {
            Content = list,
            MaxHeight = 280,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var addBtn = new Button
        {
            Content = "+ 添加官服账号",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0)
        };

        container.Children.Add(viewport);
        container.Children.Add(addBtn);
        container.Children.Add(message);

        void RenderAccounts()
        {
            list.Children.Clear();
            var meta = FeverAccountStore.Instance.LoadMetadata();
            var accounts = meta.Accounts;

            if (accounts.Count == 0)
            {
                message.Text = "暂无保存的官服账号。点击下方按钮添加，扫码或登录后即可保存。";
                return;
            }

            message.Text = "点击账号即可切换。点击“开始游戏”将以当前激活账号进入。";

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

                caption.Inlines.Add(new Run
                {
                    Text = acc.Name,
                    FontWeight = isCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
                });

                if (!string.IsNullOrEmpty(acc.AccountIdentifier))
                {
                    caption.Inlines.Add(new Run
                    {
                        Text = $" ({acc.AccountIdentifier})",
                        Foreground = Application.Current.Resources["TextFillColorTertiaryBrush"] as Brush
                    });
                }

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
                            message.Text = $"已切换为 [{acc.Name}]。点击“开始游戏”即可进入。";
                        }
                        else
                        {
                            message.Text = $"切换失败，未找到该账号的凭据备份。";
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

                // Context menu for Rename / Delete
                var menu = new MenuFlyout();
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
                        Content = $"确定要删除账号档案 [{acc.Name}] 吗？\n删除后如需再次使用该账号，需要重新扫码登录。",
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

        addBtn.Click += async (_, _) =>
        {
            addBtn.IsEnabled = false;
            message.Text = "正在准备全新登录环境…";

            nint hwnd = 0;
            try
            {
                if (Application.Current is App currentApp && currentApp.MainWindow is not null)
                    hwnd = WinRT.Interop.WindowNative.GetWindowHandle(currentApp.MainWindow);
            }
            catch { }

            // 1. Prepare clean login environment (clear cached session dbs to prevent auto-login flash crash)
            FeverAccountStore.Instance.PrepareForNewLogin();

            try
            {
                message.Text = "正在呼出网易官方登录/扫码窗口…";
                var result = await FeverLoginService.StartLoginAsync(hwnd);
                if (!result.Success || string.IsNullOrWhiteSpace(result.Ticket))
                {
                    // If canceled or failed, rollback previous account session
                    await FeverAccountStore.Instance.RollbackNewLoginAsync();
                    message.Text = result.Message ?? "登录未完成。";
                    return;
                }

                message.Text = "登录成功，正在归档保存凭据…";
                var newProfile = await FeverAccountStore.Instance.CaptureCurrentAccountAsync(ticket: result.Ticket.Trim());
                FeverIpcBridge.Instance.SetTicket(result.Ticket);
                RenderAccounts();
                message.Text = $"账号 [{newProfile.Name}] 已成功保存并激活！";
            }
            catch (Exception ex)
            {
                await FeverAccountStore.Instance.RollbackNewLoginAsync();
                message.Text = $"保存账号失败：{ex.Message}";
            }
            finally
            {
                addBtn.IsEnabled = true;
            }
        };

        RenderAccounts();
    }
}
