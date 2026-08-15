using IDVBuff.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace IDVBuff;

public static class Program
{
    private static GuiInstanceCoordinator? _guiInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack lifecycle processing must precede WinUI, logging, DI, and
        // single-instance work. Fast-exit hooks can terminate this process.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => UpdateLifecycleState.RecordCurrentVelopackInstall())
            .OnRestarted(_ =>
            {
                UpdateLifecycleState.WasRestartedAfterUpdate = true;
                UpdateLifecycleState.RecordCurrentVelopackInstall();
            })
            .Run();

        if (UpdateLifecycleState.TryRedirectLegacyLaunch(args))
            return 0;

        var isCli = args.Any(argument =>
            string.Equals(argument, "--cli", StringComparison.OrdinalIgnoreCase));
        var isIsolatedDevelopmentInstance = args.Any(argument =>
            string.Equals(argument, "--isolated-dev-instance", StringComparison.OrdinalIgnoreCase));
        if (!isCli && !isIsolatedDevelopmentInstance)
        {
            _guiInstance = new GuiInstanceCoordinator();
            if (!_guiInstance.TryAcquirePrimary())
            {
                _guiInstance.NotifyPrimaryInstance();
                _guiInstance.Dispose();
                _guiInstance = null;
                return 0;
            }
            _guiInstance.StartListening();
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        _guiInstance?.Dispose();
        return Environment.ExitCode;
    }
}
