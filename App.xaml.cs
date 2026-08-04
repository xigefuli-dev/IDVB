using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;

// Windows App SDK 单文件发布要求：在程序入口前设置此环境变量，
// 以便运行时能在单文件包内找到原生 DLL。
static class SingleFileBootstrap
{
    static SingleFileBootstrap()
    {
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);
    }
}

namespace IDVBuff
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? window;
        private bool elevationDialogOpen;
        private bool shutdownInProgress;
        private bool shutdownComplete;

        public Window MainWindow => window ?? throw new InvalidOperationException("主窗口尚未初始化。");

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                WriteStartupTrace("Creating the main window.");
                window = new Window
                {
                    Title = AppDataPaths.DisplayName,
                    ExtendsContentIntoTitleBar = false,
                    SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop()
                };
                TrySetWindowIcon(window);
                window.AppWindow.Closing += AppWindow_Closing;
                window.Closed += Window_Closed;

                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Maximize();

                var rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
                _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
                window.Activate();
                WriteStartupTrace("Main window activated.");

                await Task.Yield();
                WriteStartupTrace("Initializing map runtime.");
                MapRuntimeHost.Initialize(DispatcherQueue.GetForCurrentThread());
                MapRuntimeHost.Current.ElevationRequiredDetected += Runtime_ElevationRequiredDetected;
                await MapRuntimeHost.Current.InitializeAsync();
                WriteStartupTrace("Map runtime initialized.");
            }
            catch (Exception exception)
            {
                WriteStartupTrace("Startup failed.", exception);
                System.Diagnostics.Debug.WriteLine($"Application startup failed: {exception}");
                await ShowStartupFailureAsync(exception);
            }
        }

        private static void WriteStartupTrace(string message, Exception? exception = null)
        {
            try
            {
                var logDirectory = Path.Combine(AppDataPaths.RootDirectory, "Logs");
                Directory.CreateDirectory(logDirectory);
                var text = $"{DateTimeOffset.Now:O} {message}";
                if (exception is not null)
                    text += Environment.NewLine + exception;
                File.AppendAllText(
                    Path.Combine(logDirectory, "startup.log"),
                    text + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch
            {
                // Startup diagnostics must never make startup fail.
            }
        }

        private async Task ShowStartupFailureAsync(Exception exception)
        {
            if (window?.Content is not FrameworkElement root || root.XamlRoot is null)
                return;

            var logPath = Path.Combine(AppDataPaths.RootDirectory, "Logs", "startup.log");
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Identity Vision Bridge 启动失败",
                Content = "主窗口已经打开，但部分运行组件初始化失败。"
                    + Environment.NewLine
                    + "错误：" + exception.Message
                    + Environment.NewLine
                    + "诊断日志：" + logPath,
                CloseButtonText = "知道了"
            };
            await dialog.ShowAsync();
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

        private async void AppWindow_Closing(
            AppWindow sender,
            AppWindowClosingEventArgs args)
        {
            if (shutdownComplete)
                return;

            args.Cancel = true;
            if (shutdownInProgress)
                return;

            shutdownInProgress = true;
            try
            {
                await MapRuntimeHost.ShutdownAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Map runtime shutdown failed: {exception}");
            }
            finally
            {
                shutdownComplete = true;
                shutdownInProgress = false;
                window?.Close();
            }
        }

        private static void Window_Closed(object sender, WindowEventArgs args) =>
            MapRuntimeHost.Shutdown();

        private async void Runtime_ElevationRequiredDetected(object? sender, EventArgs e)
        {
            if (elevationDialogOpen)
                return;

            var currentWindow = window;
            if (currentWindow is null)
                return;

            elevationDialogOpen = true;
            try
            {
                FrameworkElement? root = null;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    root = currentWindow.Content as FrameworkElement;
                    if (root?.XamlRoot is not null)
                        break;
                    await Task.Delay(150);
                }
                if (root?.XamlRoot is null)
                    return;
                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = "需要管理员权限",
                    Content =
                        "检测到 dwrg.exe 的权限高于 Identity Vision Bridge，因此游戏前台无法把键盘或鼠标绑定传递给当前进程。"
                        + "是否现在请求管理员权限并重启 Identity Vision Bridge？当前设置会保留。",
                    PrimaryButtonText = "管理员重启",
                    CloseButtonText = "暂不",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
                if (MapRuntimeHost.Current.TryRestartElevated(out var failureReason))
                {
                    currentWindow.Close();
                    return;
                }

                var failure = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = "未能管理员重启",
                    Content = failureReason,
                    CloseButtonText = "知道了"
                };
                await failure.ShowAsync();
            }
            finally
            {
                elevationDialogOpen = false;
            }
        }
    }
}
