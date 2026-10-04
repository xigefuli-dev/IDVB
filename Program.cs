using IDVBuff.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using IDVBuff.Diagnostics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IDVBuff.Features.Maps;

namespace IDVBuff;

public static class Program
{
    private static GuiInstanceCoordinator? _guiInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        var mainEntered = Stopwatch.GetTimestamp();
        var mainUtc = DateTimeOffset.UtcNow;
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

        var lifecycleCompleted = Stopwatch.GetTimestamp();
        if (MapRuntimeSettingsRepository.IsLogCollectionEnabled())
            StartupTimeline.Initialize(mainEntered, mainUtc, lifecycleCompleted);
        return RunApplication(args);
    }

    // Keep WinUI and application type resolution out of the entry-point JIT.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(string[] args)
    {
        StartupTimeline.Write("Legacy launch redirect check begin (includes app-data path resolution).");
        if (UpdateLifecycleState.TryRedirectLegacyLaunch(args))
        {
            StartupTimeline.Write("Legacy launch redirected; exiting this process.");
            return 0;
        }
        StartupTimeline.Write("Legacy launch redirect check complete.");

        var isCli = args.Any(argument =>
            string.Equals(argument, "--cli", StringComparison.OrdinalIgnoreCase));
        var isIsolatedDevelopmentInstance = args.Any(argument =>
            string.Equals(argument, "--isolated-dev-instance", StringComparison.OrdinalIgnoreCase));
        if (!isCli && !isIsolatedDevelopmentInstance)
        {
            StartupTimeline.Write("GUI instance coordination begin.");
            _guiInstance = new GuiInstanceCoordinator();
            if (!_guiInstance.TryAcquirePrimary())
            {
                StartupTimeline.Write("Secondary instance: notifying primary instance.");
                _guiInstance.NotifyPrimaryInstance();
                StartupTimeline.Write("Primary notification complete; secondary instance exits.");
                _guiInstance.Dispose();
                _guiInstance = null;
                return 0;
            }
            _guiInstance.StartListening();
        }
        else if (!isCli && isIsolatedDevelopmentInstance)
        {
            StartupTimeline.Write("Dev GUI instance coordination begin.");
            _guiInstance = new GuiInstanceCoordinator(isDevelopmentInstance: true);
            if (!_guiInstance.TryAcquirePrimary())
            {
                StartupTimeline.Write("Secondary dev instance: notifying primary instance.");
                _guiInstance.NotifyPrimaryInstance();
                StartupTimeline.Write("Primary notification complete; secondary dev instance exits.");
                _guiInstance.Dispose();
                _guiInstance = null;
                return 0;
            }
            _guiInstance.StartListening();
        }

        // Lifecycle hooks and secondary processes have already exited. Only the primary
        // normal GUI owns usage accounting; CLI and isolated diagnostics do not contribute.
        using var usage = !isCli && !isIsolatedDevelopmentInstance
            ? ApplicationUsageTracker.Current
            : null;
        usage?.Start();

        StartupTimeline.Write($"Launch mode: cli={isCli}; isolatedDevelopment={isIsolatedDevelopmentInstance}.");
        if (!isCli)
        {
            var startupPreferences = MainProgramPreferences.Load();
            StartupSplash.Configure(startupPreferences.SafeMode);
            if (!startupPreferences.StartMinimized)
                StartupSplash.Show();
        }
        try
        {
            return RunWinUi();
        }
        finally
        {
            StartupTimeline.StopSampling();
            StartupSplash.Close();
            // Commit usage before releasing the primary mutex to the next process.
            usage?.Dispose();
            _guiInstance?.Dispose();
        }
    }

    // Resolve/JIT WinUI only after the independent splash thread has been started.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWinUi()
    {
        StartupSplash.Report("正在准备界面…");
        StartupTimeline.Write("COM wrappers initialization begin.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupTimeline.Write("COM wrappers initialized; Application.Start begin.");
        Application.Start(initialization =>
        {
            StartupTimeline.Write("Application.Start callback entered.");
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            StartupTimeline.StartSampling(action => dispatcher.TryEnqueue(() => action()));
            StartupTimeline.Write("App construction begin (includes Application base constructor).");
            _ = new App();
            StartupTimeline.Write("App construction complete.");
        });
        return Environment.ExitCode;
    }
}
