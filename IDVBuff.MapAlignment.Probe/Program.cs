using IDVBuff.MapAlignment.Probe.Cli;
using System.Runtime.InteropServices;

DpiAwareness.EnablePerMonitorV2();
return await CliHost.DispatchAsync(args);

internal static class DpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    public static void EnablePerMonitorV2() =>
        _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
}
