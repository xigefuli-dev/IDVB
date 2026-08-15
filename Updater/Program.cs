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

        _instanceMutex = new Mutex(true, GetInstanceMutexName(), out var ownsMutex);
        if (!ownsMutex)
            return 0;

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        var options = UpdaterLaunchOptions.Parse(Environment.GetCommandLineArgs());
        System.Windows.Forms.Application.Run(new UpdaterWindow(options));
        return 0;
    }

    private static string GetInstanceMutexName()
    {
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(executableDirectory)));
        return $"Local\\IdentityVisionBridge.Updater.{identity[..16]}";
    }
}
