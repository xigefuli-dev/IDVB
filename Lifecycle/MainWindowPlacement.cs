using System.Runtime.InteropServices;
using System.Text.Json;

namespace IDVBuff.Lifecycle;

// Normal bounds use Win32 workspace coordinates throughout; mixing AppWindow screen
// coordinates with WINDOWPLACEMENT would shift the window when the taskbar is at the top.
internal sealed record MainWindowPlacement(int Left, int Top, int Right, int Bottom, bool IsMaximized)
{
    private static readonly object SyncRoot = new();
    private static string FilePath => Path.Combine(AppDataPaths.RootDirectory, "main-window.json");

    internal bool IsValid => (long)Right - Left is > 0 and <= int.MaxValue
        && (long)Bottom - Top is > 0 and <= int.MaxValue;

    internal static MainWindowPlacement? Load(string? path = null)
    {
        lock (SyncRoot)
        {
            try
            {
                path ??= FilePath;
                if (!File.Exists(path)) return null;
                var saved = JsonSerializer.Deserialize<MainWindowPlacement>(File.ReadAllText(path));
                return saved is { IsValid: true } ? saved : null;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to load main window placement: {exception.Message}");
                return null;
            }
        }
    }

    internal void Save(string? path = null)
    {
        if (!IsValid) return;
        lock (SyncRoot)
        {
            try
            {
                path ??= FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
                File.Move(temporaryPath, path, true);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to save main window placement: {exception.Message}");
            }
        }
    }

    internal static MainWindowPlacement? Capture(IntPtr handle, bool wasMaximized)
    {
        var native = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
        if (!GetWindowPlacement(handle, ref native)) return null;
        // GetWindowPlacement's flags are always zero. Remember the last non-minimized
        // state ourselves rather than losing maximization when the user minimizes.
        var maximized = native.ShowCommand is 2 or 6 or 7 ? wasMaximized : native.ShowCommand == 3;
        var bounds = native.NormalPosition;
        var result = new MainWindowPlacement(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, maximized);
        return result.IsValid ? result : null;
    }

    internal bool Apply(IntPtr handle, bool activate = true)
    {
        if (!IsValid) return false;
        var native = new NativePlacement
        {
            Length = Marshal.SizeOf<NativePlacement>(),
            ShowCommand = activate ? (IsMaximized ? 3 : 1) : (IsMaximized ? 7 : 4),
            MinPosition = new NativePoint { X = -1, Y = -1 },
            MaxPosition = new NativePoint { X = -1, Y = -1 },
            NormalPosition = new NativeRect { Left = Left, Top = Top, Right = Right, Bottom = Bottom }
        };
        // Windows also brings completely off-screen saved bounds onto an available monitor.
        if (!SetWindowPlacement(handle, ref native)) return false;
        if (activate || !IsMaximized) return true;

        // SW_SHOWMAXIMIZED activates even a disabled WS_EX_NOACTIVATE HWND.
        // First enter the minimized state with SW_SHOWMINNOACTIVE. While it is
        // already minimized, set the documented SW_SHOWMINIMIZED + restore-to-
        // maximized flag without changing its presentation state. Finally use
        // SW_SHOWNOACTIVATE to restore; SW_SHOWNA would leave it minimized.
        // Keep rcNormalPosition with SetWindowPlacement so Windows retains its
        // workspace coordinates, off-screen recovery, and native maximize size.
        native.ShowCommand = 2;
        native.Flags = 2; // WPF_RESTORETOMAXIMIZED
        if (!SetWindowPlacement(handle, ref native)) return false;
        ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
        var restored = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
        return GetWindowPlacement(handle, ref restored) && restored.ShowCommand == 3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref NativePlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr handle, ref NativePlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
