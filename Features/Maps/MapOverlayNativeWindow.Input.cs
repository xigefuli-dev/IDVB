using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapOverlayNativeWindow
{
    internal MapScreenRect GetMonitorWorkingArea(IntPtr windowHandle)
    {
        var hMonitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
            return new MapScreenRect(0, 0, 3840, 2160);
        var monitorInfo = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfoW(hMonitor, ref monitorInfo))
            return new MapScreenRect(0, 0, 3840, 2160);
        return new MapScreenRect(
            monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Top,
            monitorInfo.rcWork.Right - monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int cbSize;
        internal RECT rcMonitor;
        internal RECT rcWork;
        internal uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);
}
