using IDVBuff.Features.Accounts;
using IDVBuff.Core.Contracts;
using IDVBuff.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff;

public partial class App
{
    private bool _accessStopping;
    private readonly CancellationTokenSource _accessLifetime = new();

    private static void AttachCliConsole()
    {
        const uint attachParentProcess = 0xFFFFFFFF;
        if (!AttachConsole(attachParentProcess)) AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), System.Text.Encoding.UTF8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), System.Text.Encoding.UTF8) { AutoFlush = true });
    }

    private async Task<bool> RequireCliVersionAccessAsync()
    {
        var access = await VersionAccessClient.CheckAsync(BuildVersionInfo.ProductVersion);
        if (access.Allowed && UsageNotice.IsAccepted()) return true;
        AttachCliConsole();
        Console.Error.WriteLine(access.Allowed
            ? "请先正常启动 Identity Vision Bridge，阅读并确认软件性质及使用责任声明。" : access.Message);
        Environment.ExitCode = IDVBuff.Cli.RealCliExitCodes.Fatal;
        Exit();
        return false;
    }

    private async Task<bool> RequireVersionAccessAsync()
    {
        if (!VersionAccessClient.Enabled) return true;

        var root = new Grid();
        root.Children.Add(new TextBlock { Text = "正在校验登录与版本权限…", HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center });
        window!.Content = root;
        var loaded = new TaskCompletionSource();
        root.Loaded += (_, _) => loaded.TrySetResult();
        window.Closed += (_, _) => { _accessLifetime.Cancel(); loaded.TrySetCanceled(); };
        StartupSplash.Close();
        window.Activate();
        await loaded.Task;
        while (!_accessLifetime.IsCancellationRequested)
        {
            var result = await VersionAccessClient.CheckAsync(BuildVersionInfo.ProductVersion, _accessLifetime.Token);
            if (result.Allowed) return true;
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = result.UpgradeRequired ? "当前版本已停止使用" : "登录与版本权限校验",
                Content = result.Message,
                PrimaryButtonText = result.LoginRequired ? "登录" : result.UpgradeRequired ? "下载新版" : "重试联网校验",
                SecondaryButtonText = result.UpgradeRequired ? "切换账户" : "",
                CloseButtonText = "退出程序"
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None) return false;
            if (choice == ContentDialogResult.Secondary) { AccountSession.Clear(); continue; }
            if (result.UpgradeRequired)
                await Windows.System.Launcher.LaunchUriAsync(new Uri(VersionAccessClient.DownloadUrl));
            else if (result.LoginRequired)
            {
                try { await AccountSession.LoginAsync(_accessLifetime.Token); }
                catch (Exception exception)
                {
                    await new ContentDialog { XamlRoot = root.XamlRoot, Title = "登录未完成",
                        Content = exception is OperationCanceledException ? "登录已取消或超时。" : exception.Message,
                        CloseButtonText = "返回" }.ShowAsync();
                }
            }
        }
        return false;
    }

    private void StartVersionAccessMonitor()
    {
        if (!VersionAccessClient.Enabled) return;

        AccountSession.Changed += VersionAccessAccountChanged;
        _ = MonitorVersionAccessAsync();
    }

    private void VersionAccessAccountChanged(object? sender, EventArgs args)
    {
        if (!VersionAccessClient.Enabled) return;

        // A late response for the previous account must never keep the runtime alive.
        if (AccountSession.PublishToken is null)
            window?.DispatcherQueue.TryEnqueue(() => _ = StopForVersionAccessAsync("已退出登录，请重新启动程序并登录。", false));
    }

    private async Task MonitorVersionAccessAsync()
    {
        if (!VersionAccessClient.Enabled) return;

        try
        {
            while (!_accessLifetime.IsCancellationRequested && !_accessStopping)
            {
                var result = await VersionAccessClient.CheckAsync(BuildVersionInfo.ProductVersion, _accessLifetime.Token);
                if (!result.Allowed)
                {
                    await StopForVersionAccessAsync(result.Message, result.UpgradeRequired);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(60), _accessLifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            WriteStartupTrace("Access monitor failed; stopping runtime.", exception);
            await StopForVersionAccessAsync("权限校验异常，请重新启动并联网校验。", false);
        }
    }

    private async Task StopForVersionAccessAsync(string message, bool upgrade)
    {
        if (!VersionAccessClient.Enabled) return;

        if (_accessStopping || shutdownInProgress || shutdownComplete) return;
        _accessStopping = true;
        explicitExitRequested = true;
        _accessLifetime.Cancel();
        AccountSession.Changed -= VersionAccessAccountChanged;
        try
        {
            // Disable the entire UI and stop background input/plugins before displaying a prompt.
            var root = new Grid();
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            window!.Content = root;
            _serviceProvider?.GetService<IGlobalInput>()?.ClearBindings();
            _pluginManager?.SetMatchActivation(false);
            _pluginManager?.Stop();
            _hostEventBridge?.Dispose();
            if (_idvbControlServer is { } control) { await control.DisposeAsync(); _idvbControlServer = null; }
            var stopSession = _serviceProvider?.GetService<Features.Maps.SessionOrchestrator>()?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            await Task.WhenAll(stopSession, StopThirdPartyPluginsAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            ShowMainWindow();
            await loaded.Task;
            var choice = await new ContentDialog
            {
                XamlRoot = root.XamlRoot, Title = upgrade ? "当前版本已停止使用，请升级" : "软件使用权限已失效",
                Content = message + "\n地图服务、热键和插件已停止。",
                PrimaryButtonText = upgrade ? "下载新版" : "", CloseButtonText = "退出程序"
            }.ShowAsync();
            if (choice == ContentDialogResult.Primary)
                await Windows.System.Launcher.LaunchUriAsync(new Uri(VersionAccessClient.DownloadUrl));
        }
        catch (Exception exception) { WriteStartupTrace("Stopping runtime after access denial.", exception); }
        finally { explicitExitRequested = true; window?.Close(); }
    }
}
