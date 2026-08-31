using System.Security.Cryptography;
using System.Text;
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

        UpdaterLaunchOptions options;
        try { options = UpdaterLaunchOptions.Parse(Environment.GetCommandLineArgs()); }
        catch (Exception exception)
        {
            UpdateLog.Write("Invalid updater launch options", exception);
            return 2;
        }

        _instanceMutex = new Mutex(true, GetInstanceMutexName(options.MapSubscriptions), out var ownsMutex);
        if (!ownsMutex)
            return 0;

        if (options.MapSubscriptions)
            return MapSubscriptionUpdater.RunAsync(options).GetAwaiter().GetResult();

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new UpdaterWindow(options));
        return 0;
    }

    private static string GetInstanceMutexName(bool mapSubscriptions)
    {
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(executableDirectory)));
        var purpose = mapSubscriptions ? "Maps" : "Application";
        return $"Local\\IdentityVisionBridge.Updater.{purpose}.{identity[..16]}";
    }
}
