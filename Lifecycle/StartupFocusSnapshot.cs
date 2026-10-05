using System.Runtime.InteropServices;

namespace IDVBuff.Lifecycle;

internal readonly record struct StartupFocusSnapshot(IntPtr ForegroundWindow, uint? LastInputTick)
{
    internal static StartupFocusSnapshot Capture()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return new StartupFocusSnapshot(GetForegroundWindow(),
            GetLastInputInfo(ref input) ? input.Time : null);
    }

    internal bool MayActivateMainWindow(StartupFocusSnapshot current, IntPtr mainWindow) =>
        mainWindow != IntPtr.Zero
        && (current.ForegroundWindow == mainWindow
            || (ForegroundWindow != IntPtr.Zero
                && current.ForegroundWindow == ForegroundWindow
                && LastInputTick.HasValue
                && current.LastInputTick == LastInputTick));

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
