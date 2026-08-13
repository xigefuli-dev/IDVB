using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace IDVBuff.Updater;

public static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // This must be the first framework call. It lets Velopack finish any
        // fast-exit lifecycle work without constructing WinUI or opening files.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        _instanceMutex = new Mutex(true, "Local\\IdentityVisionBridge.Updater", out var ownsMutex);
        if (!ownsMutex)
            return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
