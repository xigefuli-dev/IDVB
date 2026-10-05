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

    // GetLastInputInfo includes mouse movement. Its timestamp cannot tell us
    // whether the user chose another window during loading. These samples are
    // diagnostics only; they must not cancel normal startup presentation.

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
