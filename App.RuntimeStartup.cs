using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.Features.Plugins;
using IDVBuff.PluginContracts;
using IDVBuff.Cli;
using IDVBuff.Diagnostics;
using IDVBuff.Lifecycle;
using WinRT.Interop;

namespace IDVBuff;

public partial class App
{
    private Task _runtimeStartupTask = Task.CompletedTask;

    private async Task CompleteRuntimeStartupAsync(
        bool startMinimized, DispatcherQueue dispatcher, CliLaunchOptions cliOptions)
    {
        if (IsApplicationStopping || window is not { } currentWindow)
            return;
        var startupCancellation = _startupPresentationCancellation.Token;
        // ═══ 构建 DI 容器（在后台线程进行，避免大原生 DLL 首次冷加载阻塞 UI 渲染）═══
        WriteStartupTrace("DI registration begin.");
        StartupSplash.Report("正在加载运行组件…");
        var builtServices = await Task.Run(() =>
        {
            WriteStartupTrace("DI background worker started; AddIdvbServices begin.");
            var services = new ServiceCollection();
            services.AddIdvbServices(dispatcher);
            services.AddSingleton<IPluginInputService, PluginInputService>();
            WriteStartupTrace("DI: AddIdvbServices complete; BuildServiceProvider begin.");
            var sp = services.BuildServiceProvider();
            WriteStartupTrace("DI: BuildServiceProvider complete.");
            return sp;
        }, startupCancellation);
        if (!await TryCommitStartupServicesAsync(builtServices)) return;
        _servicesReadyTcs.TrySetResult();
        WriteStartupTrace("DI container built.");
        StartupSplash.Complete(StartupSplash.Stage.Services);

        _mainWindowCaptureProtection = builtServices
            .GetRequiredService<ICaptureProtectionService>()
            .RegisterWindow(
                WindowNative.GetWindowHandle(currentWindow),
                CaptureProtectionWindowCategory.MainProgram,
                "主程序窗口");

        builtServices.GetRequiredService<IOverlayNotificationService>();

        WriteStartupTrace("Capture protection registered; yielding UI dispatcher.");
        await Task.Yield();
        if (IsApplicationStopping) return;
        WriteStartupTrace("UI dispatcher continuation resumed.");

        WriteStartupTrace("Initializing map runtime.");
        StartupSplash.Report("正在准备地图服务…");

        // 新架构入口 — 唯一运行路径
        var session = builtServices.GetRequiredService<Features.Maps.SessionOrchestrator>();
        WriteStartupTrace("Map session constructed.");
        session.ElevationRequiredDetected += Runtime_ElevationRequiredDetected;
        WriteStartupTrace("Map session InitializeAsync begin.");
        await session.InitializeAsync();
        if (IsApplicationStopping) return;
        WriteStartupTrace("Map session initialized.");
        StartupSplash.Complete(StartupSplash.Stage.Maps);

        // ═══ 插件 SDK 装配（仅 GUI 路径；RealCLI 走 RunCliAsync，绝不加载插件）═══
        var pluginBus = new MessageBus();
        var pluginSynchronizer = new DispatcherQueueSynchronizer(dispatcher);
        var pluginContextFactory = new PluginContextFactory(
            pluginBus,
            pluginSynchronizer,
            builtServices);
        // 单一共享偏好存储：PluginManager 与 TTM 共用同一实例，
        // 避免两个实例各自读改写而互相覆盖整个文件。
        var preferencesStore = new PluginPreferencesStore();
        var pluginManager = new PluginManager(
            dispatcher,
            pluginBus,
            pluginContextFactory,
            preferences: preferencesStore);
        _pluginManager = pluginManager;
        // Plugin-page switches remain saved primary preferences. The
        // runtime gate only opens from the started-match control.
        pluginManager.SetMatchActivation(false);
        _teachingTipManager = new TeachingTipManager(dispatcher, preferencesStore);
        _hostEventBridge = new HostEventBridge(
            pluginBus,
            session,
            builtServices.GetRequiredService<IGlobalInput>(),
            session.SurveyCoordinator,
            builtServices.GetRequiredService<IConfigProvider>(),
            builtServices.GetRequiredService<IResolutionProfileService>());
        _hostEventBridge.Attach();
        WriteStartupTrace("Plugin host assembled; built-in registration begin.");
        StartupSplash.Report("正在加载扩展…");
        WriteStartupTrace("Built-in worker queued.");
        await Task.Run(() =>
        {
            WriteStartupTrace("Built-in worker entered (before registration method JIT).");
            startupCancellation.ThrowIfCancellationRequested();
            using (StartupTimeline.Measure("Built-in PluginRegistration.Register call"))
                PluginRegistration.Register(pluginManager);
            startupCancellation.ThrowIfCancellationRequested();
            using (StartupTimeline.Measure("Built-in PluginManager.Start call"))
                pluginManager.Start(startupCancellation);
            WriteStartupTrace("Built-in worker finished; awaiting UI continuation.");
        }, startupCancellation);
        if (IsApplicationStopping) return;
        WriteStartupTrace("Built-in UI continuation resumed.");
        WriteStartupTrace("Built-in plugins registered and started.");
        WriteStartupTrace("Third-party plugins initialization begin.");
        await InitializeThirdPartyPluginsAsync(pluginBus);
        if (IsApplicationStopping) return;
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
        if (IsApplicationStopping) return;
        await CompleteStartupPresentationAsync(startMinimized);
        if (IsApplicationStopping) return;
        StartVersionAccessMonitor();
        if (_accessStopping || IsApplicationStopping) return;
        if (!startMinimized
            && !startupElevationRequired
            && UpdateLifecycleState.WasRestartedAfterUpdate)
            await ShowUpdatedSuccessfullyAsync();
        if (_accessStopping || IsApplicationStopping) return;
        if (!startMinimized && !startupElevationRequired)
            await ShowQuickStartAsync(session);
        if (_accessStopping || IsApplicationStopping) return;
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

    private async Task DrainRuntimeStartupAsync()
    {
        try
        {
            // Yield the UI dispatcher so startup callbacks can finish before
            // any of their plugin/session/DI dependencies are disposed.
            await _runtimeStartupTask;
        }
        catch (OperationCanceledException) when (IsApplicationStopping)
        {
        }
        catch (Exception exception)
        {
            WriteStartupTrace("Runtime startup completed with an error during shutdown.", exception);
        }
    }
}
