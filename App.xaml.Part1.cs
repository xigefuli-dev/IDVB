using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Composition.SystemBackdrops;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.Features.Plugins;
using IDVBuff.Features.QuickStart;
using IDVBuff.PluginContracts;
using Microsoft.UI.Dispatching;
using IDVBuff.Diagnostics;
using IDVBuff.Cli;
using System.Runtime.InteropServices;
using IDVBuff.Lifecycle;
using WinRT.Interop;
// Windows App SDK 单文件发布要求：在程序入口前设置此环境变量，
// 以便运行时能在单文件包内找到原生 DLL。
namespace IDVBuff
{    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {

        private async void AppWindow_Closing(
            AppWindow sender,
            AppWindowClosingEventArgs args)
        {
            if (shutdownComplete)
                return;

            args.Cancel = true;
            if (!explicitExitRequested && MainProgramPreferences.Load().MinimizeToTray)
            {
                HideMainWindow();
                return;
            }
            if (shutdownInProgress)
                return;

            shutdownInProgress = true;
            var closingWindow = window;
            try
            {
                // Detach the active page tree first.  Both map editors own image
                // decode operations and native XAML surfaces; their Unloaded
                // handlers cancel that work before the runtime services go away.
                if (closingWindow is not null)
                    closingWindow.Content = null;

                // 释放新架构 SessionOrchestrator 及所有子资源
                if (_idvbControlServer is not null)
                {
                    await _idvbControlServer.DisposeAsync();
                    _idvbControlServer = null;
                }

                if (_updateShutdownServer is not null)
                {
                    await _updateShutdownServer.DisposeAsync();
                    _updateShutdownServer = null;
                }

                // TTM 持有插件设置页的 UI 实例，必须在插件停止前关闭摘除。
                _teachingTipManager?.Close();
                _teachingTipManager = null;
                await StopThirdPartyPluginsAsync();
                _pluginManager?.Stop();
                _pluginManager = null;
                _hostEventBridge?.Dispose();
                _hostEventBridge = null;
                DisposeSafeModeTraditionalWindowInput();

                if (_serviceProvider?.GetService<Features.Maps.SessionOrchestrator>() is IAsyncDisposable ad)
                {
                    await ad.DisposeAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(8));
                }

                if (_serviceProvider is { } sp)
                {
                    await sp.DisposeAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(8));
                    _serviceProvider = null;
                }

            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Map runtime shutdown failed: {exception}");
            }
            finally
            {
                _serviceProvider = null;
                // These two caches contain native image allocations.  They are
                // process-wide, so disposing the DI graph alone cannot release
                // them when a WinUI window has kept the process alive.  Keep
                // this cleanup in finally so a timed-out service cannot skip it.
                MapStructurePreprocessor.ClearReferenceCache();
                MapOverlayBitmapRenderer.InvalidateImageCache();
                shutdownComplete = true;
                shutdownInProgress = false;
                try
                {
                    if (closingWindow is not null)
                        closingWindow.Close();
                    else
                        CompleteApplicationExit();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Main window close failed: {exception}");
                    CompleteApplicationExit();
                }
            }
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            _mainWindowCaptureProtection?.Dispose();
            _mainWindowCaptureProtection = null;
            if (sender is Window closedWindow)
            {
                closedWindow.AppWindow.Closing -= AppWindow_Closing;
                closedWindow.AppWindow.Changed -= AppWindow_Changed;
                closedWindow.Closed -= Window_Closed;
                closedWindow.Content = null;
            }
            window = null;
            CompleteApplicationExit();
        }

        private void CompleteApplicationExit()
        {
            if (applicationExitRequested)
                return;

            applicationExitRequested = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            GuiInstanceCoordinator.ActivationRequested -= GuiInstance_ActivationRequested;
            OutputLog.Shutdown();
            if (ReferenceEquals(_currentApp, this))
                _currentApp = null;

            // A WinUI desktop process can remain alive when another hidden
            // XAML window or dispatcher is still registered.  Closing the main
            // HWND is therefore followed by an explicit application exit. If
            // WinUI only posts that request, terminate after all owned services
            // and logs have already completed their bounded cleanup above.
            Exit();
            Environment.Exit(Environment.ExitCode);
        }

        private void GuiInstance_ActivationRequested(object? sender, EventArgs e)
        {
            window?.DispatcherQueue.TryEnqueue(ShowMainWindow);
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPresenterChange
                && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
                && MainProgramPreferences.Load().MinimizeToTray)
                HideMainWindow();
        }

        private void ShowMainWindow()
        {
            var currentWindow = window;
            if (currentWindow is null)
                return;
            if (!mainWindowHasBeenShown)
            {
                SetMainWindowCloaked(false);
                ShowWindow(WindowNative.GetWindowHandle(currentWindow), 5);
                currentWindow.Activate();
                mainWindowHasBeenShown = true;
                if (currentWindow.AppWindow.Presenter is OverlappedPresenter initialPresenter)
                    initialPresenter.Maximize();
                return;
            }
            ShowWindow(WindowNative.GetWindowHandle(currentWindow), 5);
            if (currentWindow.AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Restore();
            currentWindow.Activate();
        }

        private void HideMainWindow()
        {
            if (window is { } currentWindow)
                ShowWindow(WindowNative.GetWindowHandle(currentWindow), 0);
        }

        private void SetMainWindowCloaked(bool cloaked)
        {
            if (window is null || mainWindowIsCloaked == cloaked)
                return;
            var value = cloaked ? 1 : 0;
            if (DwmSetWindowAttribute(
                    WindowNative.GetWindowHandle(window),
                    13,
                    ref value,
                    sizeof(int)) == 0)
                mainWindowIsCloaked = cloaked;
        }

        private void RequestApplicationExit()
        {
            explicitExitRequested = true;
            window?.Close();
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        private async Task ShowUpdatedSuccessfullyAsync()
        {
            UpdateLifecycleState.WasRestartedAfterUpdate = false;
            if (window?.Content is not FrameworkElement root)
                return;
            await new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "更新完成",
                Content = $"Identity Vision Bridge 已更新到 {BuildVersionInfo.BuildVersion}。",
                CloseButtonText = "知道了"
            }.ShowAsync();
        }

        private async Task ShowQuickStartAsync(Features.Maps.SessionOrchestrator session)
        {
            var stateStore = new QuickStartStateStore();
            if (!stateStore.ShouldShow)
                return;

            FrameworkElement? root = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                root = window?.Content as FrameworkElement;
                if (root?.XamlRoot is not null)
                    break;
                await Task.Delay(100);
            }

            var choice = await QuickStartDialog.ShowAsync(root?.XamlRoot);
            if (choice is null)
                return;

            if (choice == QuickStartChoice.UseRecommendedSettings)
            {
                try
                {
                    await ApplyQuickStartSelectionAsync(session);
                    if (window?.Content is Frame { Content: MainPage mainPage })
                        await mainPage.ShowRecommendedConfigurationGuideAsync();
                }
                catch (Exception exception)
                {
                    WriteStartupTrace("Unable to apply quick-start recommended settings.", exception);
                    return;
                }
            }

            try
            {
                stateStore.MarkCompleted();
            }
            catch (Exception exception)
            {
                // A marker failure must not prevent the application from starting.
                WriteStartupTrace("Unable to persist quick-start completion.", exception);
            }
        }

        private void Runtime_ElevationRequiredDetected(object? sender, EventArgs e)
        {
            // The integrity check runs during SessionOrchestrator initialization.
            // Defer the mandatory dialog until the rest of OnLaunched has completed.
            startupElevationRequired = true;
            WriteStartupTrace("Startup requires administrator privileges.");
        }

        private async Task ShowStartupElevationRequiredAsync()
        {
            var currentWindow = window;
            try
            {
                if (currentWindow is null)
                    return;

                FrameworkElement? root = null;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    root = currentWindow.Content as FrameworkElement;
                    if (root?.XamlRoot is not null)
                        break;
                    await Task.Delay(150);
                }

                if (root?.XamlRoot is not null)
                {
                    await new ContentDialog
                    {
                        XamlRoot = root.XamlRoot,
                        Title = "需要管理员权限",
                        Content = "Identity Vision Bridge 必须以管理员权限运行，请退出后重新以管理员权限打开。",
                        CloseButtonText = "退出",
                        DefaultButton = ContentDialogButton.Close
                    }.ShowAsync();
                }
            }
            catch (Exception exception)
            {
                WriteStartupTrace("Unable to show the administrator privilege prompt.", exception);
            }
            finally
            {
                RequestApplicationExit();
            }
        }
    }
}
