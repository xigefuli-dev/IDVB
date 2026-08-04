using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>Captures the visible foreground dwrg.exe client area without activating it.</summary>
public sealed class DwrGameWindowCaptureService
{
    private const string ProcessName = "dwrg";

    public bool TryGetForegroundClientBounds(
        out MapScreenRect clientBounds,
        out IntPtr windowHandle,
        out string failureReason)
    {
        clientBounds = default;
        windowHandle = IntPtr.Zero;
        if (!TryGetForegroundGameWindow(out var window, out failureReason))
            return false;
        if (!TryGetClientBounds(window, out clientBounds))
        {
            failureReason = "无法读取 dwrg.exe 游戏窗口的客户区。";
            return false;
        }
        windowHandle = window;
        return true;
    }

    public bool TryCaptureClient(out CapturedGameFrame? frame, out string failureReason) =>
        TryCapture(viewport: null, out frame, out failureReason);

    public bool TryCaptureViewport(
        NormalizedRectangle viewport,
        out CapturedGameFrame? frame,
        out string failureReason) =>
        TryCapture(viewport, out frame, out failureReason);

    private static bool TryCapture(
        NormalizedRectangle? viewport,
        out CapturedGameFrame? frame,
        out string failureReason)
    {
        frame = null;
        failureReason = string.Empty;
        if (!TryGetForegroundGameWindow(out var window, out failureReason))
            return false;
        if (!TryGetClientBounds(window, out var clientBounds))
        {
            failureReason = "无法读取 dwrg.exe 游戏窗口的客户区。";
            return false;
        }

        var viewportBounds = viewport?.IsValid is true
            ? GetViewportBounds(clientBounds, viewport)
            : clientBounds;
        if (!viewportBounds.IsValid)
        {
            failureReason = "已校准的地图区域无效，请重新校准。";
            return false;
        }

        try
        {
            using var bitmap = new Bitmap(
                (int)Math.Round(viewportBounds.Width),
                (int)Math.Round(viewportBounds.Height),
                PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    (int)Math.Round(viewportBounds.X),
                    (int)Math.Round(viewportBounds.Y),
                    0,
                    0,
                    bitmap.Size,
                    CopyPixelOperation.SourceCopy);
            }
            if (GetForegroundWindow() != window)
            {
                failureReason = "捕获期间前台窗口发生变化，已丢弃这一帧，请保持游戏地图打开后重试。";
                return false;
            }
            frame = new CapturedGameFrame(
                BitmapConverter.ToMat(bitmap),
                clientBounds,
                viewportBounds,
                window);
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"无法捕获 dwrg.exe 游戏画面：{exception.Message}";
            return false;
        }
    }

    private static bool TryGetForegroundGameWindow(out IntPtr window, out string failureReason)
    {
        window = GetForegroundWindow();
        failureReason = string.Empty;
        if (window == IntPtr.Zero)
        {
            failureReason = "未找到前台窗口，请返回游戏并打开完整地图。";
            return false;
        }
        if (!IsWindowVisible(window) || IsIconic(window))
        {
            failureReason = "游戏窗口不可见或已最小化，请返回游戏并打开完整地图。";
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "dwrg.exe 不是前台窗口，请返回游戏并打开完整地图。";
                return false;
            }
        }
        catch
        {
            failureReason = "无法确认前台游戏进程，请返回游戏后重试。";
            return false;
        }
        return true;
    }

    public static MapScreenRect GetViewportBounds(
        MapScreenRect client,
        NormalizedRectangle viewport)
    {
        var left = Math.Clamp(
            (int)Math.Floor(viewport.X * client.Width),
            0,
            Math.Max(0, (int)client.Width - 1));
        var top = Math.Clamp(
            (int)Math.Floor(viewport.Y * client.Height),
            0,
            Math.Max(0, (int)client.Height - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling((viewport.X + viewport.Width) * client.Width),
            left + 1,
            (int)client.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((viewport.Y + viewport.Height) * client.Height),
            top + 1,
            (int)client.Height);
        return new MapScreenRect(
            client.X + left,
            client.Y + top,
            right - left,
            bottom - top);
    }

    public static uint GetWindowDpi(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return 96;
        var dpi = GetDpiForWindow(window);
        return dpi == 0 ? 96u : dpi;
    }

    /// <summary>
    /// Restores the foreground game window after a temporary manual-selection
    /// window closes. The next global game-map input is ignored unless the
    /// game is foreground, so this must happen before presenting the result.
    /// </summary>
    public static void RestoreForegroundWindow(IntPtr window)
    {
        if (window != IntPtr.Zero)
            SetForegroundWindow(window);
    }

    private static bool TryGetClientBounds(IntPtr window, out MapScreenRect bounds)
    {
        bounds = default;
        if (!GetClientRect(window, out var client) || client.Right <= client.Left || client.Bottom <= client.Top)
            return false;
        var origin = new NativePoint { X = client.Left, Y = client.Top };
        if (!ClientToScreen(window, ref origin))
            return false;
        bounds = new MapScreenRect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
