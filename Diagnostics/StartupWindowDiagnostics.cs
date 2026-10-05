using System.Runtime.InteropServices;
using System.Text;

namespace IDVBuff.Diagnostics;

/// <summary>Read-only snapshots of startup HWNDs; never changes shell/taskbar state.</summary>
internal static class StartupWindowDiagnostics
{
    internal static string Describe(string stage, IntPtr window)
    {
        var foreground = GetForegroundWindow();
        GetCursorPos(out var cursor);
        var hit = WindowFromPoint(cursor);
        GetWindowRect(window, out var rectangle);
        var monitorHandle = MonitorFromWindow(window, 2);
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitorHandle, ref monitor);
        var cloakResult = DwmGetWindowAttribute(window, 14, out var cloak, sizeof(int));
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return $"Startup HWND state: stage={stage}; hwnd=0x{window.ToInt64():X}; class={name}; "
            + $"foreground=0x{foreground.ToInt64():X}; pointerHit=0x{hit.ToInt64():X}; "
            + $"pointer=({cursor.X},{cursor.Y}); visible={IsWindowVisible(window)}; enabled={IsWindowEnabled(window)}; "
            + $"style=0x{GetWindowLong(window, -16):X8}; exstyle=0x{GetWindowLong(window, -20):X8}; "
            + $"cloaked={(cloakResult == 0 ? cloak.ToString() : $"unavailable({cloakResult:X8})")}; "
            + $"rect={rectangle}; monitor={monitor.Monitor}; workArea={monitor.WorkArea}; "
            + $"taskbars=[{DescribeTaskbars()}].";
    }

    private static string DescribeTaskbars()
    {
        var result = new StringBuilder();
        var primary = FindWindow("Shell_TrayWnd", null);
        result.Append(DescribeTaskbar("primary", primary));
        var previous = IntPtr.Zero;
        // Bound enumeration even if shell windows change while this read-only snapshot is taken.
        for (var index = 0; index < 16; index++)
        {
            var secondary = FindWindowEx(IntPtr.Zero, previous, "Shell_SecondaryTrayWnd", null);
            if (secondary == IntPtr.Zero)
                break;
            result.Append("; ").Append(DescribeTaskbar($"secondary-{index}", secondary));
            previous = secondary;
        }
        return result.ToString();
    }

    private static string DescribeTaskbar(string name, IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return $"{name}:absent";
        GetWindowRect(handle, out var rectangle);
        GetWindowThreadProcessId(handle, out var process);
        return $"{name}:hwnd=0x{handle.ToInt64():X},pid={process},visible={IsWindowVisible(handle)},"
            + $"rect={rectangle},previous=0x{GetWindow(handle, 3).ToInt64():X},"
            + $"next=0x{GetWindow(handle, 2).ToInt64():X}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left; public int Top; public int Right; public int Bottom;
        public override readonly string ToString() => $"({Left},{Top},{Right},{Bottom})";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size; public Rect Monitor; public Rect WorkArea; public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? name);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
