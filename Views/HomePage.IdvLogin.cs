using IDVBuff.Plugins.IdvLogin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
    private TeachingTip? _loginTip;

    private FrameworkElement CreateGameLaunchActions()
    {
        var accounts = new Button
        {
            Width = 58,
            Height = 58,
            Content = new SymbolIcon(Symbol.Contact),
            CornerRadius = new CornerRadius(8)
        };
        ToolTipService.SetToolTip(accounts, "通过 idv-login 扫码或选择账号登录");
        AutomationProperties.SetName(accounts, "账号登录");
        var slot = new Grid { Width = 58, Height = 58, Children = { accounts } };
        var setLoading = CreateAccountLoadingOutline(slot);
        var loading = false;
        var manager = App.Plugins;
        void UpdateVisibility(object? sender, EventArgs args)
        {
            var enabled = manager?.IsEnabled(IdvLoginPlugin.PluginId) == true;
            slot.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled) setLoading(false);
            if (!enabled && _loginTip is not null) _loginTip.IsOpen = false;
        }
        UpdateVisibility(null, EventArgs.Empty);
        Loaded += async (_, _) =>
        {
            await App.MainWindowPresentationCompleted;
            if (!IsLoaded) return;
            if (manager is not null) manager.EnabledChanged -= UpdateVisibility;
            manager = App.Plugins;
            if (manager is not null)
            {
                manager.EnabledChanged -= UpdateVisibility;
                manager.EnabledChanged += UpdateVisibility;
            }
            UpdateVisibility(null, EventArgs.Empty);
        };
        var host = new Grid();
        accounts.Click += async (_, _) =>
        {
            if (loading) return;
            if (_loginTip is not null) { _loginTip.IsOpen = false; return; }
            loading = true;
            setLoading(true);
            AutomationProperties.SetName(accounts, "正在加载账号");
            try { await ShowLoginAccountsAsync(host, accounts, () => setLoading(false)); }
            finally
            {
                loading = false;
                setLoading(false);
                AutomationProperties.SetName(accounts, "账号登录");
            }
        };
        host.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _launchGameButton, slot }
        });
        Unloaded += (_, _) =>
        {
            setLoading(false);
            if (manager is not null) manager.EnabledChanged -= UpdateVisibility;
            if (_loginTip is not null) _loginTip.IsOpen = false;
            _loginTip = null;
        };
        return host;
    }

    private async Task ShowLoginAccountsAsync(Panel host, Button anchor, Action finishLoading)
    {
        if (_loginTip is not null) { _loginTip.IsOpen = false; return; }
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
        var body = new StackPanel { Spacing = 12, Width = 280 };
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
        AutomationProperties.SetName(back, "返回上一级");
        ToolTipService.SetToolTip(back, "返回上一级");
        var content = new StackPanel { Spacing = 8, Width = 280, Children = { back, body } };
        var tip = new TeachingTip
        {
            Target = anchor,
            IsLightDismissEnabled = true,
            PreferredPlacement = TeachingTipPlacementMode.Bottom,
            Content = content
        };
        _loginTip = tip;
        Action goBack = () => tip.IsOpen = false;
        back.Click += (_, _) => goBack();
        host.Children.Add(tip);
        tip.Closed += (_, _) => { host.Children.Remove(tip); if (_loginTip == tip) _loginTip = null; };
        var manager = App.Plugins;
        if (manager?.TryGet(IdvLoginPlugin.PluginId, out var registered) != true ||
            registered is not IdvLoginPlugin plugin)
        { body.Children.Add(new TextBlock { Text = "账号登录插件暂不可用。" }); tip.IsOpen = true; return; }
        if (!manager.IsEnabled(plugin.Id))
        {
            host.Children.Remove(tip);
            _loginTip = null;
            return;
        }
        var pluginLifetime = plugin.Lifetime;
        try
        {
            var cleanup = await AttachLoginAccountsAsync(tip, body, plugin, message, handler => goBack = handler);
            finishLoading();
            if (_loginTip == tip && IsLoaded && !pluginLifetime.IsCancellationRequested) tip.IsOpen = true;
            else
            {
                cleanup();
                // A page unload or disable during initial loading must not reopen the tip.
                host.Children.Remove(tip);
                if (_loginTip == tip) _loginTip = null;
            }
        }
        finally { finishLoading(); }
    }

}
