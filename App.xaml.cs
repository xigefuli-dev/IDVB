using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Composition.SystemBackdrops;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Feedback;
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

// Windows App SDK 单文件发布要求：在程序入口前设置此环境变量，以便运行时能在单文件包内找到原生 DLL。
static class SingleFileBootstrap
{
    static SingleFileBootstrap() =>
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
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
        public Window MainWindow => window ?? throw new InvalidOperationException("主窗口尚未初始化。");

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            WriteStartupTrace("App constructor entered; output logging initialization begin.");
            _currentApp = this;
            var isCliLaunch = Array.Exists(
                Environment.GetCommandLineArgs(),
                argument => string.Equals(argument, "--cli", StringComparison.OrdinalIgnoreCase));
            // First-chance exception capture is intentionally diagnostic-only.
            // Enabling it for every production GUI process turns a handled
            // exception loop into a high-volume allocation and disk-write loop.
            InitializeOutputLog(isCliLaunch);
            SavedDiagnosticDataRetention.Start();
            WriteStartupTrace("Output logging initialized.");
            OfficialFeedbackService.TokenProvider = () => Features.Accounts.AccountSession.PublishToken;
            OfficialFeedbackService.ClientVersionProvider = () => BuildVersionInfo.BuildVersion;
            UnhandledException += App_UnhandledException;
            WriteStartupTrace("App XAML InitializeComponent begin.");
            this.InitializeComponent();
            WriteStartupTrace("App XAML InitializeComponent complete.");
            StartupSplash.Complete(StartupSplash.Stage.Interface);
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
                WriteStartupTrace("OnLaunched entered; parsing launch options.");
                var cliOptions = CliLaunchOptions.Parse(Environment.GetCommandLineArgs());
                if (cliOptions.IsCli)
                {
                    if (!await RequireCliVersionAccessAsync()) return;
                    await RunCliAsync(cliOptions);
                    return;
                }

                WriteStartupTrace("Creating the main window.");
                WriteStartupTrace("Preferences load begin.");
                var preferences = MainProgramPreferences.Load(); IsSafeMode = preferences.SafeMode;
                OutputLog.ConfigureApplication(
                    BuildVersionInfo.ProductVersion, BuildVersionInfo.BuildVersion, IsSafeMode);
                WriteStartupTrace($"Preferences loaded: safeMode={IsSafeMode}; startMinimized={preferences.StartMinimized}.");
                PluginRandomDelayPolicy.AllowUnsafeMinimums = !IsSafeMode && preferences.AllowUnsafePluginRandomDelayMinimums; var startMinimized = preferences.StartMinimized;
                var isIsolatedDevelopmentInstance = Environment.GetCommandLineArgs().Any(argument => string.Equals(argument, "--isolated-dev-instance", StringComparison.OrdinalIgnoreCase));
                WriteStartupTrace("Window construction and backdrop begin.");
                window = new Window
                {
                    Title = isIsolatedDevelopmentInstance
                        ? $"{AppDataPaths.DisplayName} [DEV {BuildVersionInfo.BuildVersion}]"
                        : AppDataPaths.DisplayName,
                    ExtendsContentIntoTitleBar = false,
                    SystemBackdrop = FluentTheme.CreateWindowBackdrop(preferences.UseLegacyTheme)
                };
                WriteStartupTrace("Window constructed; icon setup begin.");
                TrySetWindowIcon(window);
                WriteStartupTrace("Window icon setup complete.");
                if (!await RequireUsageNoticeAsync())
                {
                    Exit();
                    return;
                }
                window.Closed += (_, _) => StopStartupRenderObservation();
                if (!await RequireVersionAccessAsync()) { Exit(); return; }
                window.AppWindow.Closing += AppWindow_Closing;
                window.AppWindow.Changed += AppWindow_Changed;
                window.Closed += Window_Closed;

                // Initialize the visual tree without showing a large blank window.
                SetMainWindowCloaked(true);
                _startupPresentationPending = true;

                if (!startMinimized)
                {
                    StartupSplash.SetTargetWindow(WindowNative.GetWindowHandle(window));
                }

                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Maximize();

                WriteStartupTrace("Window presentation configured; startup content construction begin.");
                var rootFrame = new Frame { RequestedTheme = AppThemePreference.Resolve(preferences) };
                _mainFrame = rootFrame;
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
                if (!rootFrame.Navigate(typeof(Views.MainPage), e.Arguments))
                    throw new InvalidOperationException("主界面导航失败。");
                WriteStartupTrace("Main page navigated; Window.Activate begin.");
                window.Activate();
                if (!startMinimized)
                {
                    WriteStartupTrace("Main window activated while cloaked; native startup splash remains visible.");
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
                WriteStartupTrace("Window services started.");
                StartupSplash.Complete(StartupSplash.Stage.Window);
                WriteStartupTrace("Model improvement initialization begin.");
                await InitializeModelImprovementAsync(preferences, startMinimized);
                WriteStartupTrace("Model improvement initialization complete; safe-mode branch begin.");
                if (await TryCompleteSafeModeLaunchAsync(startMinimized, preferences))
                {
                    if (!explicitExitRequested)
                    {
                        await CompleteStartupPresentationAsync(startMinimized);
                    }
                    return;
                }
                // ═══ 构建 DI 容器（在后台线程进行，避免大原生 DLL 首次冷加载阻塞 UI 渲染）═══
                WriteStartupTrace("DI registration begin.");
                StartupSplash.Report("正在加载运行组件…");
                _serviceProvider = await Task.Run(() =>
                {
                    WriteStartupTrace("DI background worker started; AddIdvbServices begin.");
                    var services = new ServiceCollection();
                    services.AddIdvbServices(dispatcher);
                    services.AddSingleton<IPluginInputService, PluginInputService>();
                    WriteStartupTrace("DI: AddIdvbServices complete; BuildServiceProvider begin.");
                    var sp = services.BuildServiceProvider();
                    WriteStartupTrace("DI: BuildServiceProvider complete.");
                    return sp;
                });
                _servicesReadyTcs.TrySetResult();
                WriteStartupTrace("DI container built.");
                StartupSplash.Complete(StartupSplash.Stage.Services);

                _mainWindowCaptureProtection = _serviceProvider
                    .GetRequiredService<ICaptureProtectionService>()
                    .RegisterWindow(
                        WindowNative.GetWindowHandle(window),
                        CaptureProtectionWindowCategory.MainProgram,
                        "主程序窗口");

                _serviceProvider.GetRequiredService<IOverlayNotificationService>();

                WriteStartupTrace("Capture protection registered; yielding UI dispatcher.");
                await Task.Yield();
                WriteStartupTrace("UI dispatcher continuation resumed.");

                WriteStartupTrace("Initializing map runtime.");
                StartupSplash.Report("正在准备地图服务…");

                // 新架构入口 — 唯一运行路径
                var session = _serviceProvider.GetRequiredService<Features.Maps.SessionOrchestrator>();
                WriteStartupTrace("Map session constructed.");
                session.ElevationRequiredDetected += Runtime_ElevationRequiredDetected;
                WriteStartupTrace("Map session InitializeAsync begin.");
                await session.InitializeAsync();
                WriteStartupTrace("Map session initialized.");
                StartupSplash.Complete(StartupSplash.Stage.Maps);

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
                WriteStartupTrace("Plugin host assembled; built-in registration begin.");
                StartupSplash.Report("正在加载扩展…");
                WriteStartupTrace("Built-in worker queued.");
                await Task.Run(() =>
                {
                    WriteStartupTrace("Built-in worker entered (before registration method JIT).");
                    using (StartupTimeline.Measure("Built-in PluginRegistration.Register call"))
                        PluginRegistration.Register(_pluginManager);
                    using (StartupTimeline.Measure("Built-in PluginManager.Start call"))
                        _pluginManager.Start();
                    WriteStartupTrace("Built-in worker finished; awaiting UI continuation.");
                });
                WriteStartupTrace("Built-in UI continuation resumed.");
                WriteStartupTrace("Built-in plugins registered and started.");
                WriteStartupTrace("Third-party plugins initialization begin.");
                await InitializeThirdPartyPluginsAsync(pluginBus);
                WriteStartupTrace("Third-party plugins initialization complete.");
                StartupSplash.Complete(StartupSplash.Stage.Extensions);
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
                StartupSplash.Report("正在完成准备…");
                await PrepareMapListAsync(session);
                await CompleteStartupPresentationAsync(startMinimized);
                StartVersionAccessMonitor();
                if (_accessStopping) return;
                if (!startMinimized
                    && !startupElevationRequired
                    && UpdateLifecycleState.WasRestartedAfterUpdate)
                    await ShowUpdatedSuccessfullyAsync();
                if (_accessStopping) return;
                if (!startMinimized && !startupElevationRequired)
                    await ShowQuickStartAsync(session);
                if (_accessStopping) return;
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
                _startupPresentationPending = false;
                StartupSplash.Close();
                StartupTimeline.StopSampling();
                if (window is not null) ShowMainWindow();
                StopStartupRenderObservation();
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
            _ = Features.Accounts.VersionAccessClient.MonitorHeadlessAsync(BuildVersionInfo.ProductVersion, cancellation.Token);
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        private static void App_UnhandledException(
            object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            try
            {
                MapGlobalInputService.EmergencyUnhookAll();
            }
            catch
            {
            }

            OutputLog.Write("ERROR", "WINUI", "Unhandled UI exception.", args.Exception);
            // No recovery has taken place. Let WinUI terminate rather than
            // continuing with detached input hooks and inconsistent UI state.
            args.Handled = false;
        }

        private async Task ShowStartupFailureAsync(Exception exception)
        {
            if (window?.Content is not FrameworkElement root || root.XamlRoot is null)
                return;

            var logPath = StartupLogPath;
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Identity Vision Bridge 启动失败",
                Content = "主窗口已经打开，但部分运行组件初始化失败。"
                    + Environment.NewLine
                    + "错误：" + exception.Message
                    + Environment.NewLine
                    + "诊断日志：" + logPath,
                CloseButtonText = "关闭提示"
            };
            await dialog.ShowAsync();
        }

        // This application is Windows-only; the fallback is intentionally built
        // from WinUI controls so it can still render when authored XAML fails.
#pragma warning disable CA1416
        private bool ShowStartupFailurePage(Exception exception)
        {
            if (_mainFrame is not { } rootFrame
                || _startupPlaceholder is null)
                return false;

            try
            {
                var logPath = StartupLogPath;
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
                if (_startupHost is { } host && _startupPlaceholder is { } placeholder)
                    host.Children.Remove(placeholder);
                _startupPlaceholder = null;
                window?.Activate();
                return true;
            }
            catch (Exception fallbackException)
            {
                WriteStartupTrace("Unable to show the startup failure page.", fallbackException);
                return false;
            }
        }
#pragma warning restore CA1416
    }
}
