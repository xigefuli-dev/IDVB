using IDVBuff.Plugins.IdvLogin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
    private async Task<Action> AttachLoginAccountsAsync(TeachingTip tip, StackPanel content,
        IdvLoginPlugin plugin, TextBlock message, Action<Action> setBack)
    {
        content.Children.Clear();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(plugin.Lifetime);
        var token = lifetime.Token;
        var list = new StackPanel { Spacing = 6 };
        var add = new Button { Content = "+ 添加账号", HorizontalAlignment = HorizontalAlignment.Stretch };
        var viewport = new ScrollViewer
        {
            Content = list, MaxHeight = 320,
            Visibility = Visibility.Collapsed,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        content.Children.Add(viewport);
        content.Children.Add(add);
        content.Children.Add(message);
        message.FontSize = 12;
        message.Text = "正在读取…";
        var navigation = new LoginPanelNavigation();
        var busy = false;
        var refreshing = false;
        var signature = "";
        var lastConnectionNotice = "";
        LoginImportState? rendered = null;
        bool IsCurrent(int revision) => !token.IsCancellationRequested && navigation.IsCurrent(revision);

        async Task RefreshAsync()
        {
            if (refreshing || busy || token.IsCancellationRequested) return;
            refreshing = true;
            var revision = navigation.Revision;
            try
            {
                if (plugin.ImportState is { } state && navigation.Page == LoginPanelPage.Import)
                {
                    if (rendered != state)
                    {
                        var currentRevision = revision;
                        var qrImage = state.QrBase64.Length > 0 ? await DecodeLoginQrAsync(state.QrBase64) : null;
                        if (!IsCurrent(currentRevision)) return;
                        rendered = state;
                        list.Children.Clear();
                        if (qrImage is not null)
                            list.Children.Add(new Image { Source = qrImage, Width = 220, Height = 220 });
                        viewport.Visibility = qrImage is null ? Visibility.Collapsed : Visibility.Visible;
                        message.Text = state.UserMessage;
                        if (state.Success)
                        {
                            plugin.ClearCompletedImport();
                            navigation.Navigate(LoginPanelPage.Accounts);
                            signature = "";
                            add.Visibility = Visibility.Visible;
                        }
                    }
                    return;
                }
                if (navigation.Page != LoginPanelPage.Accounts) return;
                var items = await plugin.GetAccountsAsync(token);
                if (!IsCurrent(revision)) return;
                var next = System.Text.Json.JsonSerializer.Serialize(items);
                if (next != signature)
                {
                    list.Children.Clear();
                    foreach (var account in items)
                    {
                        var brand = LoginBrand.For(account.Channel);
                        var caption = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                        caption.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                        {
                            Text = brand.Name,
                            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                                255, (byte)(brand.Color >> 16), (byte)(brand.Color >> 8), (byte)brand.Color))
                        });
                        caption.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " · " + account.Name });
                        var row = new Button
                        {
                            Content = caption,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left
                        };
                        row.Click += async (_, _) =>
                        {
                            if (busy) return;
                            // Starting/focusing the game must not dismiss the tip and
                            // cancel the pending selection. Explicit Back/Close still can.
                            tip.IsLightDismissEnabled = false;
                            if (IsGameRunning())
                            {
                                tip.IsLightDismissEnabled = true;
                                message.Text = "请先退出游戏，再点账号重新启动";
                                return;
                            }
                            var loginRevision = navigation.Revision;
                            busy = true;
                            foreach (var control in list.Children.OfType<Control>()) control.IsEnabled = false;
                            add.IsEnabled = false;
                            message.Text = "正在启动游戏…";
                            try
                            {
                                await plugin.LaunchWithAccountAsync(account.Id, token);
                                if (IsCurrent(loginRevision))
                                    message.Text = "已请求启动，等待游戏自动登录";
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                            catch (OperationCanceledException)
                            { if (IsCurrent(loginRevision)) message.Text = "启动响应超时，请先确认游戏是否已打开"; }
                            catch (LoginLaunchException ex)
                            { if (IsCurrent(loginRevision)) message.Text = ex.Message; }
                            catch { if (IsCurrent(loginRevision)) message.Text = "未确认启动结果，请先检查游戏是否已打开"; }
                            finally
                            {
                                busy = false;
                                tip.IsLightDismissEnabled = true;
                                foreach (var control in list.Children.OfType<Control>()) control.IsEnabled = true;
                                add.IsEnabled = true;
                            }
                        };
                        list.Children.Add(row);
                    }
                    signature = next;
                    viewport.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                    message.Text = items.Count == 0 ? "暂无渠道账号" : "点击账号登录";
                }
                var notice = plugin.ConnectionStatus;
                if (!string.IsNullOrEmpty(notice)) message.Text = notice;
                else if (lastConnectionNotice.Length > 0 && message.Text == lastConnectionNotice)
                    message.Text = items.Count == 0 ? "暂无渠道账号" : "点击账号登录";
                lastConnectionNotice = notice;
                add.IsEnabled = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { if (IsCurrent(revision)) { message.Text = string.IsNullOrEmpty(plugin.ConnectionStatus)
                ? "连接失败，正在重试…" : plugin.ConnectionStatus; signature = ""; } }
            finally { refreshing = false; }
        }

        async Task ShowChannelsAsync(bool navigate = true)
        {
            if (navigate) navigation.Navigate(LoginPanelPage.Channels);
            var revision = navigation.Revision;
            list.Children.Clear();
            viewport.Visibility = Visibility.Collapsed;
            add.Visibility = Visibility.Collapsed;
            message.Text = "正在读取渠道…";
            try
            {
                var channels = await plugin.GetChannelsAsync(token);
                if (!IsCurrent(revision)) return;
                foreach (var channel in channels)
                {
                    var row = new Button { Content = channel.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
                    row.Click += (_, _) =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (plugin.ImportState is { Completed: false } pending && pending.Channel != channel.Id)
                        { message.Text = "另一个渠道仍在验证，请稍后重试"; return; }
                        try
                        {
                            plugin.StartImport(channel);
                            navigation.Navigate(LoginPanelPage.Import);
                            rendered = null;
                            list.Children.Clear();
                            viewport.Visibility = Visibility.Collapsed;
                            message.Text = channel.SupportsInlineQr ? "正在生成二维码…" : "正在打开授权窗口…";
                            _ = RefreshAsync();
                        }
                        catch { message.Text = "无法添加账号"; }
                    };
                    list.Children.Add(row);
                }
                message.Text = "选择账号渠道";
                viewport.Visibility = channels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { if (IsCurrent(revision)) message.Text = "无法读取渠道，请返回重试"; }
        }
        add.Click += async (_, _) => await ShowChannelsAsync();
        setBack(async () =>
        {
            if (!navigation.Back()) { tip.IsOpen = false; return; }
            if (navigation.Page == LoginPanelPage.Channels)
            {
                await ShowChannelsAsync(navigate: false);
                return;
            }
            // Navigation never waits for, cancels, or clears the remote import.
            signature = "";
            rendered = null;
            list.Children.Clear();
            viewport.Visibility = Visibility.Collapsed;
            add.Visibility = Visibility.Visible;
            message.Text = "正在读取…";
            _ = RefreshAsync();
        });
        if (plugin.ImportState is { Completed: false })
        {
            navigation.Navigate(LoginPanelPage.Import);
            add.Visibility = Visibility.Collapsed;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await RefreshAsync();
        var disabled = token.Register(() => DispatcherQueue.TryEnqueue(() => tip.IsOpen = false));
        var cleaned = false;
        void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            timer.Stop();
            disabled.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
        }
        tip.Closed += (_, _) => Cleanup();
        // Prepare the first snapshot before opening, including any resumed QR.
        await RefreshAsync();
        if (!token.IsCancellationRequested) timer.Start();
        return Cleanup;
    }

    private static async Task<BitmapImage> DecodeLoginQrAsync(string base64)
    {
        if (base64.Length > 1024 * 1024) throw new InvalidDataException();
        var bytes = Convert.FromBase64String(base64);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = 440, DecodePixelHeight = 440 };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
