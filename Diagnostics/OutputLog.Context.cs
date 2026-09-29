using System.Globalization;
using System.Runtime.InteropServices;

namespace IDVBuff.Diagnostics;

public static partial class OutputLog
{
    private static string? productVersion;
    private static string? buildVersion;
    private static bool? applicationSafeMode;
    private static string? scanLogPath;
    private static string deviceResolution = "unknown";
    private static CaptureContext? captureContext;

    private readonly record struct CaptureContext(
        int ClientWidth, int ClientHeight,
        int RegionWidth, int RegionHeight, IntPtr Monitor);

    /// <summary>Uses the running application's compiled versions and effective safe mode.</summary>
    public static void ConfigureApplication(string product, string build, bool safeMode)
    {
        lock (Gate)
        {
            if (productVersion == product && buildVersion == build && applicationSafeMode == safeMode)
                return;
            productVersion = product;
            buildVersion = build;
            applicationSafeMode = safeMode;
            WriteContext("Application context updated");
        }
    }

    public static void BindScanLog(string path)
    {
        lock (Gate)
        {
            if (scanLogPath == path)
                return;
            scanLogPath = path;
            WriteContext("Scan log associated");
        }
    }

    public static void UnbindScanLog(string path)
    {
        lock (Gate)
        {
            // A late finalization of an older collector must not clear a newer session.
            if (scanLogPath != path)
                return;
            WriteContext("Scan log association ended");
            scanLogPath = null;
        }
    }

    /// <summary>Records the pixel dimensions resolved by the actual viewport crop rules.</summary>
    public static void UpdateCaptureContext(
        int clientWidth, int clientHeight, int regionWidth, int regionHeight,
        IntPtr windowHandle)
    {
        if (writer is null)
            return;
        try
        {
            var monitor = windowHandle == IntPtr.Zero
                ? IntPtr.Zero : MonitorFromWindow(windowHandle, 2);
            var resolution = ReadDeviceResolution(monitor);
            lock (Gate)
            {
                var next = new CaptureContext(clientWidth, clientHeight, regionWidth, regionHeight, monitor);
                if (next == captureContext && resolution == deviceResolution)
                    return;
                captureContext = next;
                deviceResolution = resolution;
                WriteContext("Recognition region updated");
            }
        }
        catch
        {
            // Native display diagnostics must never affect capture.
        }
    }

    public static void ClearCaptureContext()
    {
        lock (Gate)
        {
            if (captureContext is null)
                return;
            captureContext = null;
            deviceResolution = ReadDeviceResolution(IntPtr.Zero);
            WriteContext("Recognition region unavailable");
        }
    }

    public static Dictionary<string, object?> GetContextDetails()
    {
        lock (Gate)
        {
            return new()
            {
                ["productVersion"] = productVersion ?? "unknown",
                ["buildVersion"] = buildVersion ?? "unknown",
                ["applicationSafeMode"] = applicationSafeMode,
                ["deviceResolution"] = deviceResolution,
                ["deviceResolutionSource"] = captureContext is { Monitor: var monitor } && monitor != IntPtr.Zero
                    ? "game-window-monitor" : "primary-monitor",
                ["clientWidth"] = captureContext?.ClientWidth,
                ["clientHeight"] = captureContext?.ClientHeight,
                ["recognitionRegionWidth"] = captureContext?.RegionWidth,
                ["recognitionRegionHeight"] = captureContext?.RegionHeight,
                ["recognitionRegionState"] = captureContext is not null ? "resolved"
                    : applicationSafeMode == true ? "disabled-by-safe-mode" : "awaiting-game-window",
                ["scanLogFile"] = scanLogPath is null ? "none" : Path.GetFileName(scanLogPath),
                ["scanLogPath"] = scanLogPath,
                ["outputLogFile"] = CurrentLogPath is null ? "none" : Path.GetFileName(CurrentLogPath),
                ["outputLogPath"] = CurrentLogPath
            };
        }
    }

    private static void WriteContext(string message)
    {
        if (writer is null)
            return;
        Write("INFO", "CONTEXT", message + " | " + string.Join(" | ",
            GetContextDetails().Select(pair => pair.Key + "=" + (pair.Value is null ? "unknown" : pair.Value is bool flag
                ? flag.ToString().ToLowerInvariant()
                : Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "unknown"))));
    }

    private static string ReadDeviceResolution(IntPtr monitor)
    {
        try
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                return $"{info.Bounds.Right - info.Bounds.Left}x{info.Bounds.Bottom - info.Bounds.Top}";
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            return width > 0 && height > 0 ? $"{width}x{height}" : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

}
