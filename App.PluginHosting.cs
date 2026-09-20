using IDVBuff.Diagnostics;
using IDVBuff.Features.Plugins.V2;
using IDVBuff.PluginContracts;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;
using Microsoft.Extensions.DependencyInjection;

namespace IDVBuff;

public partial class App
{
    private ThirdPartyHostEventBridge? _thirdPartyHostEventBridge;
    private ThirdPartyPluginRuntimeManager? _thirdPartyPluginRuntime;
    private IdvpInstaller? _thirdPartyPluginInstaller;
    private PluginStateRepository? _thirdPartyPluginState;
    private PluginDirectories? _thirdPartyPluginDirectories;
    private PluginNotificationCenter? _pluginNotificationCenter;

    public static ThirdPartyPluginRuntimeManager? ThirdPartyPlugins =>
        _currentApp?._thirdPartyPluginRuntime;

    public static IdvpInstaller? ThirdPartyPluginInstaller =>
        _currentApp?._thirdPartyPluginInstaller;

    public static PluginStateRepository? ThirdPartyPluginState =>
        _currentApp?._thirdPartyPluginState;

    public static PluginDirectories? ThirdPartyPluginDirectories =>
        _currentApp?._thirdPartyPluginDirectories;

    public static PluginNotificationCenter? PluginNotifications =>
        _currentApp?._pluginNotificationCenter;

    private async Task InitializeThirdPartyPluginsAsync(IMessageBus pluginBus)
    {
        using var timing = StartupTimeline.Measure("Third-party initialization body (after method JIT)");
        WriteStartupTrace("Third-party directories, state and installer construction begin.");
        // Developer mode is explicit and uses isolated package, trust and state directories.
        var pluginDeveloperMode = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--plugin-developer-mode", StringComparison.OrdinalIgnoreCase));
        _thirdPartyPluginDirectories = new PluginDirectories(
            AppDataPaths.RootDirectory,
            pluginDeveloperMode);
        _thirdPartyPluginState = new PluginStateRepository(_thirdPartyPluginDirectories);
        _thirdPartyPluginInstaller = new IdvpInstaller(
            _thirdPartyPluginDirectories,
            _thirdPartyPluginState,
            BuildVersionInfo.ProductVersion);
        var thirdPartyEventHub = new ThirdPartyHostEventHub();
        WriteStartupTrace("Third-party directories, state and installer constructed; event bridge begin.");
        _thirdPartyHostEventBridge = new ThirdPartyHostEventBridge(pluginBus, thirdPartyEventHub);
        _thirdPartyHostEventBridge.Attach();
        _pluginNotificationCenter = new PluginNotificationCenter();
        var serviceProvider = _serviceProvider
            ?? throw new InvalidOperationException("DI container is not initialized.");
        WriteStartupTrace("Third-party event bridge attached; capability services resolution begin.");
        var capabilitySource = new ThirdPartyPluginCapabilitySource(
            thirdPartyEventHub,
            serviceProvider.GetRequiredService<IPluginInputService>(),
            serviceProvider.GetRequiredService<IPluginScreenshotService>(),
            _pluginNotificationCenter,
            QueueThirdPartyPluginFault);
        WriteStartupTrace("Third-party capability services resolved; context and manager construction begin.");
        var contextFactory = new DefaultThirdPartyPluginContextFactory(
            capabilitySource,
            manifest => new DelegatePluginLogger((level, message, exception) =>
                OutputLog.Write(
                    level >= PluginLogLevel.Error ? "ERROR" : level >= PluginLogLevel.Warning ? "WARN" : "INFO",
                    $"PLUGIN/{manifest.Id}",
                    exception is null ? message : $"{message}{Environment.NewLine}{exception}")),
            QueueThirdPartyPluginFault);
        _thirdPartyPluginRuntime = new ThirdPartyPluginRuntimeManager(
            _thirdPartyPluginDirectories,
            _thirdPartyPluginState,
            _thirdPartyPluginInstaller,
            contextFactory)
        {
            StartupDiagnostic = message => WriteStartupTrace($"Third-party: {message}")
        };
        WriteStartupTrace("Third-party manager constructed.");
        try
        {
            using (StartupTimeline.Measure("Third-party activation gate close"))
                await _thirdPartyPluginRuntime.SetMatchActivationAsync(false);
            using (StartupTimeline.Measure("Third-party runtime StartAsync call"))
                await _thirdPartyPluginRuntime.StartAsync();
        }
        catch (Exception exception)
        {
            OutputLog.Write("ERROR", "PLUGIN/HOST", $"Third-party plugin startup was disabled: {exception}");
        }
    }

    private async Task SetMatchPluginActivationAsync(bool active)
    {
        var builtIn = _pluginManager;
        var thirdParty = _thirdPartyPluginRuntime;
        var previous = thirdParty?.IsMatchActivationAllowed ?? false;
        try
        {
            builtIn?.SetMatchActivation(active);
            if (thirdParty is not null)
                await thirdParty.SetMatchActivationAsync(active);
        }
        catch
        {
            if (thirdParty is not null && thirdParty.IsMatchActivationAllowed != previous)
                await thirdParty.SetMatchActivationAsync(previous);
            builtIn?.SetMatchActivation(previous);
            throw;
        }
    }

    private void QueueThirdPartyPluginFault(string pluginId, Exception exception)
    {
        var runtime = _thirdPartyPluginRuntime;
        if (runtime is null)
            return;
        _ = ReportAsync();
        return;

        async Task ReportAsync()
        {
            try
            {
                await runtime.ReportFaultAsync(
                    pluginId,
                    $"Plugin callback failed: {exception.GetBaseException().Message}");
            }
            catch (Exception reportException)
            {
                OutputLog.Write(
                    "ERROR",
                    "PLUGIN/HOST",
                    $"Could not quarantine plugin {pluginId}: {reportException}");
            }
        }
    }

    private async Task StopThirdPartyPluginsAsync()
    {
        if (_thirdPartyPluginRuntime is not null)
        {
            await _thirdPartyPluginRuntime.StopAsync();
            _thirdPartyPluginRuntime = null;
        }
        _thirdPartyHostEventBridge?.Dispose();
        _thirdPartyHostEventBridge = null;
        _pluginNotificationCenter = null;
    }
}
