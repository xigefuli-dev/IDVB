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
        private bool startupElevationRequired;
        private bool shutdownInProgress;
        private bool shutdownComplete;
        private bool applicationExitRequested;
        private bool explicitExitRequested;
        private bool mainWindowHasBeenShown;
        private bool mainWindowIsCloaked;
        private ServiceProvider? _serviceProvider;
        private IdvbControlServer? _idvbControlServer;
        private UpdateShutdownServer? _updateShutdownServer;
        private PluginManager? _pluginManager;
        private TeachingTipManager? _teachingTipManager;
        private HostEventBridge? _hostEventBridge;
        private ICaptureProtectionRegistration? _mainWindowCaptureProtection;
        private TrayIconController? _trayIcon;

        /// <summary>全局 DI 容器（供 Views 等非 DI 感知组件使用）。</summary>
        public static ServiceProvider Services =>
            (_currentApp?._serviceProvider)
            ?? throw new InvalidOperationException("DI 容器尚未构建。");

        /// <summary>快捷访问新架构入口（供 Views 使用）。</summary>
        public static Features.Maps.SessionOrchestrator Session =>
            Services.GetRequiredService<Features.Maps.SessionOrchestrator>();

        /// <summary>快捷访问插件宿主（供插件管理页读取已注册插件）。</summary>
        public static PluginManager? Plugins => _currentApp?._pluginManager;
        /// <summary>快捷访问插件设置 TeachingTip 管理器（供插件管理页挂载/触发设置页）。</summary>
        public static TeachingTipManager? TeachingTips => _currentApp?._teachingTipManager;

        private static App? _currentApp;
        public Window MainWindow => window ?? throw new InvalidOperationException("主窗口尚未初始化。");

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            _currentApp = this;
            var isCliLaunch = Array.Exists(
                Environment.GetCommandLineArgs(),
                argument => string.Equals(argument, "--cli", StringComparison.OrdinalIgnoreCase));
            // First-chance exception capture is intentionally diagnostic-only.
            // Enabling it for every production GUI process turns a handled
            // exception loop into a high-volume allocation and disk-write loop.
            OutputLog.Initialize(
                captureFirstChanceExceptions: !isCliLaunch
                    && (System.Diagnostics.Debugger.IsAttached || AppDataPaths.IsTestBuild));
            UnhandledException += App_UnhandledException;
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
                var cliOptions = CliLaunchOptions.Parse(Environment.GetCommandLineArgs());
                if (cliOptions.IsCli)
                {
                    await RunCliAsync(cliOptions);
                    return;
                }

                WriteStartupTrace("Creating the main window.");
                var preferences = MainProgramPreferences.Load(); IsSafeMode = preferences.SafeMode;
                PluginRandomDelayPolicy.AllowUnsafeMinimums = !IsSafeMode && preferences.AllowUnsafePluginRandomDelayMinimums; var startMinimized = preferences.StartMinimized;
                var isIsolatedDevelopmentInstance = Environment.GetCommandLineArgs().Any(argument =>
                    string.Equals(argument, "--isolated-dev-instance", StringComparison.OrdinalIgnoreCase));
                window = new Window
                {
                    Title = isIsolatedDevelopmentInstance
                        ? $"{AppDataPaths.DisplayName} [DEV {BuildVersionInfo.BuildVersion}]"
                        : AppDataPaths.DisplayName,
                    ExtendsContentIntoTitleBar = false,
                    SystemBackdrop = FluentTheme.CreateWindowBackdrop(preferences.UseLegacyTheme)
                };
                TrySetWindowIcon(window);
                window.AppWindow.Closing += AppWindow_Closing;
                window.AppWindow.Changed += AppWindow_Changed;
                window.Closed += Window_Closed;

                if (startMinimized)
                {
                    SetMainWindowCloaked(true);
                }

                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Maximize();

                var rootFrame = new Frame { RequestedTheme = AppThemePreference.Resolve(preferences) };
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
                _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
                window.Activate();
                if (!startMinimized)
                {
                    mainWindowHasBeenShown = true;
                    WriteStartupTrace("Main window activated.");
                }
                else
                {
                    WriteStartupTrace("Main window starts hidden in the notification area.");
                }
                WriteStartupTrace(
                    $"SystemBackdrop support — Acrylic: {DesktopAcrylicController.IsSupported()}, Mica: {MicaController.IsSupported()}");

                var dispatcher = DispatcherQueue.GetForCurrentThread();
                _trayIcon = new TrayIconController(
                    dispatcher,
                    ShowMainWindow,
                    RequestApplicationExit);
                GuiInstanceCoordinator.ActivationRequested += GuiInstance_ActivationRequested;
                _updateShutdownServer = new UpdateShutdownServer(() =>
                    dispatcher.TryEnqueue(RequestApplicationExit));
                _updateShutdownServer.Start();
                await InitializeModelImprovementAsync(preferences, startMinimized);
                if (await TryCompleteSafeModeLaunchAsync(startMinimized, preferences))
                    return;
                // ═══ 构建 DI 容器 ═══
                var services = new ServiceCollection();
                services.AddIdvbServices(dispatcher);
                services.AddSingleton<IPluginInputService, PluginInputService>();
                _serviceProvider = services.BuildServiceProvider();
                WriteStartupTrace("DI container built.");

                _mainWindowCaptureProtection = _serviceProvider
                    .GetRequiredService<ICaptureProtectionService>()
                    .RegisterWindow(
                        WindowNative.GetWindowHandle(window),
                        CaptureProtectionWindowCategory.MainProgram,
                        "主程序窗口");

                await Task.Yield();
                WriteStartupTrace("Initializing map runtime.");

                // 新架构入口 — 唯一运行路径
                var session = _serviceProvider.GetRequiredService<Features.Maps.SessionOrchestrator>();
                session.ElevationRequiredDetected += Runtime_ElevationRequiredDetected;
                await session.InitializeAsync();

                // ═══ 插件 SDK 装配（仅 GUI 路径；RealCLI 走 RunCliAsync，绝不加载插件）═══
                var pluginBus = new MessageBus();
                var pluginSynchronizer = new DispatcherQueueSynchronizer(dispatcher);
                var pluginContextFactory = new PluginContextFactory(
                    pluginBus,
                    pluginSynchronizer,
                    _serviceProvider);
                // 单一共享偏好存储：PluginManager 与 TTM 共用同一实例，
                // 避免两个实例各自读改写而互相覆盖整个文件。
                var preferencesStore = new PluginPreferencesStore();
                _pluginManager = new PluginManager(
                    dispatcher,
                    pluginBus,
                    pluginContextFactory,
                    preferences: preferencesStore);
                // Plugin-page switches remain saved primary preferences. The
                // runtime gate only opens from the started-match control.
                _pluginManager.SetMatchActivation(false);
                _teachingTipManager = new TeachingTipManager(dispatcher, preferencesStore);
                _hostEventBridge = new HostEventBridge(
                    pluginBus,
                    session,
                    _serviceProvider.GetRequiredService<IGlobalInput>(),
                    session.SurveyCoordinator,
                    _serviceProvider.GetRequiredService<IConfigProvider>(),
                    _serviceProvider.GetRequiredService<IResolutionProfileService>());
                _hostEventBridge.Attach();
                PluginRegistration.Register(_pluginManager);
                _pluginManager.Start();

                await InitializeThirdPartyPluginsAsync(pluginBus);
                session.MatchPluginActivationChanged += SetMatchPluginActivationAsync;

                if (!string.IsNullOrWhiteSpace(cliOptions.IdvbControlPipeName))
                {
                    _idvbControlServer = new IdvbControlServer(
                        cliOptions.IdvbControlPipeName,
                        dispatcher,
                        session);
                    _idvbControlServer.Start();
                    WriteStartupTrace(
                        $"IDVB control pipe started: {cliOptions.IdvbControlPipeName}");
                }

                WriteStartupTrace("Map runtime initialized.");
                if (!startMinimized
                    && !startupElevationRequired
                    && UpdateLifecycleState.WasRestartedAfterUpdate)
                    await ShowUpdatedSuccessfullyAsync();
                if (!startMinimized && !startupElevationRequired)
                    await ShowQuickStartAsync(session);
                StartStartupBackgroundTasks(session);
                if (startMinimized)
                {
                    HideMainWindow();
                    SetMainWindowCloaked(false);
                }

                if (startupElevationRequired)
                {
                    // A minimized launch has no visible owner for the dialog.
                    // Show the main window only for this mandatory startup prompt.
                    if (startMinimized)
                        ShowMainWindow();
                    await ShowStartupElevationRequiredAsync();
                }
            }
            catch (Exception exception)
            {
                WriteStartupTrace("Startup failed.", exception);
                System.Diagnostics.Debug.WriteLine($"Application startup failed: {exception}");
                if (ShowStartupFailurePage(exception))
                    return;

                await ShowStartupFailureAsync(exception);
            }
        }

        private async Task RunCliAsync(CliLaunchOptions options)
        {
            OutputLog.Shutdown();
            AttachCliConsole();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            OutputLog.Initialize(captureFirstChanceExceptions: false);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            };
            Console.CancelKeyPress += cancelHandler;
            var exitCode = RealCliExitCodes.Fatal;

            try
            {
                WriteStartupTrace("Starting the RealCLI runtime host.");
                if (options.IdvbAttachPipeName is not null)
                {
                    await using var remote = new RemoteRealCliClient(options);
                    exitCode = await remote.RunAsync(cancellation.Token);
                }
                else
                {
                    var dispatcher = DispatcherQueue.GetForCurrentThread();
                    var services = new ServiceCollection();
                    services.AddIdvbServices(dispatcher, headless: true);
                    _serviceProvider = services.BuildServiceProvider();

                    var session = _serviceProvider.GetRequiredService<Features.Maps.SessionOrchestrator>();
                    await session.InitializeAsync();

                    await using (var host = new RealCliHost(session, options))
                    {
                        exitCode = await host.RunAsync(cancellation.Token);
                    }
                }
            }
            catch (Exception exception)
            {
                WriteStartupTrace("RealCLI startup failed.", exception);
                Console.Error.WriteLine(exception.ToString());
                exitCode = RealCliExitCodes.Fatal;
            }
            finally
            {
                try
                {
                    if (_serviceProvider is not null)
                    {
                        await _serviceProvider.DisposeAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(8));
                    }
                }
                catch (Exception exception)
                {
                    WriteStartupTrace("RealCLI shutdown failed.", exception);
                    exitCode = RealCliExitCodes.Fatal;
                }
                finally
                {
                    _serviceProvider = null;
                    Console.CancelKeyPress -= cancelHandler;
                    OutputLog.Shutdown();
                }
            }

            Environment.ExitCode = exitCode;
            Environment.Exit(exitCode);
        }

        private static void AttachCliConsole()
        {
            const uint AttachParentProcess = 0xFFFFFFFF;
            if (!AttachConsole(AttachParentProcess))
                AllocConsole();

            var output = new StreamWriter(
                Console.OpenStandardOutput(),
                System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
            var error = new StreamWriter(
                Console.OpenStandardError(),
                System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
            Console.SetOut(output);
            Console.SetError(error);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        private static void WriteStartupTrace(string message, Exception? exception = null)
        {
            OutputLog.Write(
                exception is null ? "INFO" : "ERROR",
                "STARTUP",
                message,
                exception);
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

        private static void App_UnhandledException(
            object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
            OutputLog.Write("ERROR", "WINUI", "Unhandled UI exception.", args.Exception);

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

        // This application is Windows-only; the fallback is intentionally built
        // from WinUI controls so it can still render when authored XAML fails.
#pragma warning disable CA1416
        private bool ShowStartupFailurePage(Exception exception)
        {
            if (window?.Content is not Frame rootFrame || rootFrame.Content is not null)
                return false;

            try
            {
                var logPath = Path.Combine(AppDataPaths.RootDirectory, "Logs", "startup.log");
                var content = new StackPanel
                {
                    MaxWidth = 760,
                    Padding = new Thickness(40),
                    Spacing = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                content.Children.Add(new TextBlock
                {
                    Text = "Identity Vision Bridge 启动失败",
                    FontSize = 28,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                content.Children.Add(new TextBlock
                {
                    Text = "主界面未能加载。请将下面的错误与诊断日志一并反馈。",
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = exception.GetBaseException().Message,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"诊断日志：{logPath}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                });

                rootFrame.Content = content;
                window.Activate();
                return true;
            }
            catch (Exception fallbackException)
            {
                WriteStartupTrace("Unable to show the startup failure page.", fallbackException);
                return false;
            }
        }
#pragma warning restore CA1416

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
