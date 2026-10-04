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
using System.IO;

// Windows App SDK 单文件发布要求：在程序入口前设置此环境变量，
// 以便运行时能在单文件包内找到原生 DLL。
namespace IDVBuff
{
    /// <summary>
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

            SaveMainWindowPlacement();
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

            // 立即隐藏主窗口与系统托盘图标，从用户视觉上瞬间关闭（< 10ms），绝无空白卡顿或“未响应”
            if (closingWindow is not null)
            {
                try
                {
                    closingWindow.AppWindow.Hide();
                    ShowWindow(WindowNative.GetWindowHandle(closingWindow), 0);
                }
                catch { }
            }

            try
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch { }
            GuiInstanceCoordinator.ActivationRequested -= GuiInstance_ActivationRequested;

            // Watchdog: If async disposal stalls or deadlocks, force terminate after 5 seconds.
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            {
                try { CompleteApplicationExit(); }
                catch { }
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
                catch { }
            }, TaskScheduler.Default);

            try
            {
                // Detach the active page tree first while window is already hidden.
                if (closingWindow is not null)
                {
                    try { closingWindow.Content = null; }
                    catch { }
                }

                try { RealtimePerformanceOverlay.Instance?.Dispose(); }
                catch { }
                try { IDVBuff.Features.Notifications.OverlayNotificationWindow.Instance?.Dispose(); }
                catch { }

                // 释放新架构 SessionOrchestrator 及所有子资源
                if (_idvbControlServer is not null)
                {
                    try { await _idvbControlServer.DisposeAsync(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"IDVB control server dispose failed: {ex}"); }
                    finally { _idvbControlServer = null; }
                }

                if (_updateShutdownServer is not null)
                {
                    try { await _updateShutdownServer.DisposeAsync(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Update shutdown server dispose failed: {ex}"); }
                    finally { _updateShutdownServer = null; }
                }

                // TTM 持有插件设置页的 UI 实例，必须在插件停止前关闭摘除。
                try
                {
                    _teachingTipManager?.Close();
                    _teachingTipManager = null;
                }
                catch { }

                try
                {
                    await StopThirdPartyPluginsAsync().WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Stop third party plugins failed: {ex}"); }

                try
                {
                    _pluginManager?.Stop();
                    _pluginManager = null;
                }
                catch { }

                try
                {
                    _hostEventBridge?.Dispose();
                    _hostEventBridge = null;
                }
                catch { }

                try { DisposeSafeModeTraditionalWindowInput(); }
                catch { }

                if (_serviceProvider?.GetService<Features.Maps.SessionOrchestrator>() is IAsyncDisposable ad)
                {
                    try
                    {
                        await ad.DisposeAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromMilliseconds(1500));
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SessionOrchestrator dispose failed: {ex}"); }
                }

                if (_serviceProvider is { } sp)
                {
                    try
                    {
                        await sp.DisposeAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(1));
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ServiceProvider dispose failed: {ex}"); }
                    finally { _serviceProvider = null; }
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
                try
                {
                    MapStructurePreprocessor.ClearReferenceCache();
                    MapOverlayBitmapRenderer.InvalidateImageCache();
                }
                catch { }

                shutdownComplete = true;
                shutdownInProgress = false;
                try
                {
                    if (closingWindow is not null)
                    {
                        closingWindow.Closed -= Window_Closed;
                        closingWindow.Close();
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Main window close failed: {exception}");
                }
                finally
                {
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
            try { _trayIcon?.Dispose(); } catch { }
            _trayIcon = null;
            GuiInstanceCoordinator.ActivationRequested -= GuiInstance_ActivationRequested;
            try { OutputLog.Shutdown(); } catch { }
            if (ReferenceEquals(_currentApp, this))
                _currentApp = null;

            // A WinUI desktop process can remain alive when another hidden
            // XAML window or dispatcher is still registered. Closing the main
            // HWND is therefore followed by an explicit application exit. If
            // WinUI only posts that request, terminate after all owned services
            // and logs have already completed their bounded cleanup above.
            try { Exit(); }
            catch { }
            try { Environment.Exit(Environment.ExitCode); }
            catch { }
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { }
        }

        private void GuiInstance_ActivationRequested(object? sender, EventArgs e)
        {
            window?.DispatcherQueue.TryEnqueue(ShowMainWindow);
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (mainWindowHasBeenShown && !shutdownInProgress
                && (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
                && sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized })
                CaptureMainWindowPlacement();
            if (args.DidPresenterChange
                && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
                && MainProgramPreferences.Load().MinimizeToTray)
                HideMainWindow();
        }

        private void ShowMainWindow() => ShowMainWindow(bringToForeground: true);

        private void ShowMainWindow(bool bringToForeground)
        {
            if (_startupPresentationPending)
                return;
            var currentWindow = window;
            if (currentWindow is null)
                return;
            var hWnd = WindowNative.GetWindowHandle(currentWindow);
            if (!mainWindowHasBeenShown)
            {
                SetMainWindowCloaked(false);
                ShowWindow(hWnd, bringToForeground ? 5 : 8);
                mainWindowHasBeenShown = true;
                CaptureMainWindowPlacement();
                if (bringToForeground)
                {
                    currentWindow.Activate();
                    BringWindowToForeground(hWnd);
                }
                return;
            }
            SetMainWindowCloaked(false);
            ShowWindow(hWnd, bringToForeground ? 5 : 8);
            if (currentWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                    presenter.Restore();
            }
            if (bringToForeground)
            {
                currentWindow.Activate();
                BringWindowToForeground(hWnd);
            }
        }

        private void BringWindowToForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            var foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == hWnd) return;

            var foregroundThreadId = foregroundHwnd != IntPtr.Zero ? GetWindowThreadProcessId(foregroundHwnd, out _) : 0;
            var currentThreadId = GetCurrentThreadId();
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, true);
                try { BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
                finally { AttachThreadInput(currentThreadId, foregroundThreadId, false); }
            }
            else
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
        }

        private void HideMainWindow()
        {
            SaveMainWindowPlacement();
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

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr windowHandle, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int attributeValue, int attributeSize);

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
            }.ShowThemedAsync();
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
                    if (_mainFrame?.Content is MainPage mainPage)
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
                    }.ShowThemedAsync();
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

        private static void TrySetWindowIcon(Window targetWindow)
        {
            var iconPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Icons",
                "IDVB_icon_multisize.ico");

            if (!File.Exists(iconPath))
                return;

            try
            {
                targetWindow.AppWindow.SetIcon(iconPath);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to set IDVB icon: {exception.Message}");
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
