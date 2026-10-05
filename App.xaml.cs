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
using IDVBuff.Presentation.Theming;
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
                _startupLaunchFocus = StartupSplash.LaunchFocus ?? StartupFocusSnapshot.Capture();
                WriteStartupTrace("Preferences load begin.");
                var preferences = MainProgramPreferences.Load(); IsSafeMode = preferences.SafeMode;
                OutputLog.ConfigureApplication(
                    BuildVersionInfo.ProductVersion, BuildVersionInfo.BuildVersion, IsSafeMode);
                ThemeService.Initialize(preferences.GetAppearance(), DispatcherQueue.GetForCurrentThread());
                WriteStartupTrace($"Preferences loaded: safeMode={IsSafeMode}; startMinimized={preferences.StartMinimized}.");
                PluginRandomDelayPolicy.AllowUnsafeMinimums = !IsSafeMode && preferences.AllowUnsafePluginRandomDelayMinimums; var startMinimized = preferences.StartMinimized;
                var isIsolatedDevelopmentInstance = Program.IsDevelopmentInstance;
                WriteStartupTrace("Window construction and backdrop begin.");
                window = new Window
                {
                    Title = isIsolatedDevelopmentInstance
                        ? $"{AppDataPaths.DisplayName} [DEV {BuildVersionInfo.BuildVersion}]"
                        : AppDataPaths.DisplayName,
                    ExtendsContentIntoTitleBar = false
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
                BeginStartupWindowInteractionGuard();

                RestoreMainWindowPlacement();
                WriteStartupWindowState("main-placement-restored");

                WriteStartupTrace("Window presentation configured; startup content construction begin.");
                var rootFrame = new Frame();
                var mainTheme = ThemeService.AttachWindow(window, rootFrame, useBackdrop: true);
                rootFrame.Background = mainTheme.WindowBrush;
                _mainFrame = rootFrame;
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
                if (!rootFrame.Navigate(typeof(Views.MainPage), e.Arguments))
                    throw new InvalidOperationException("主界面导航失败。");
                WriteStartupTrace("Main page navigated; showing guarded HWND without activation.");
                if (!startMinimized)
                {
                    window.AppWindow.Show(false);
                    WriteStartupTrace("Main window shown while cloaked and input-disabled; native startup splash remains visible.");
                }
                else
                {
                    window.AppWindow.Hide();
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
                if (IsApplicationStopping) return;
                WriteStartupTrace("Model improvement initialization complete; safe-mode branch begin.");
                if (await TryCompleteSafeModeLaunchAsync(startMinimized, preferences))
                {
                    if (!IsApplicationStopping)
                    {
                        await CompleteStartupPresentationAsync(startMinimized);
                    }
                    return;
                }
                _runtimeStartupTask = CompleteRuntimeStartupAsync(startMinimized, dispatcher, cliOptions);
                await _runtimeStartupTask;
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException || !IsApplicationStopping)
                {
                    WriteStartupTrace("Startup failed.", exception);
                    System.Diagnostics.Debug.WriteLine($"Application startup failed: {exception}");
                }
                _servicesReadyTcs.TrySetException(exception);
                _mainWindowPresentationCompleted.TrySetException(exception);
                ReleaseStartupWindowInteractionGuard();
                _startupPresentationPending = false;
                StartupSplash.Close();
                StartupTimeline.StopSampling();
                if (IsApplicationStopping)
                    return;
                if (window is not null)
                    ShowMainWindow(bringToForeground: MayActivateStartupMainWindow());
                StopStartupRenderObservation();
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
            await dialog.ShowThemedAsync();
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
