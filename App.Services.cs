using Microsoft.Extensions.DependencyInjection;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Plugins;
using IDVBuff.Lifecycle;
using IDVBuff.Cli;

namespace IDVBuff;

public partial class App
{
    private static App? _currentApp;
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

    private static readonly TaskCompletionSource _servicesReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>当全局 DI 容器与基础服务完成构建时触发的 Task。</summary>
    public static Task ServicesReadyTask => _servicesReadyTcs.Task;
    /// <summary>全局 DI 容器是否已完成构建并可用。</summary>
    public static bool IsServicesReady => _currentApp?._serviceProvider is not null;

    private async ValueTask<bool> TryCommitStartupServicesAsync(ServiceProvider services)
    {
        if (IsApplicationStopping)
        {
            await services.DisposeAsync();
            return false;
        }
        _serviceProvider = services;
        return true;
    }

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
}
