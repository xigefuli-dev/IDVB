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

    // 抓屏 surface 复用：`new Bitmap` + `Graphics.FromImage` 在每次抓屏里实测
    // 约占 4.3ms（1600x900），就绪轮询/稳定帧循环每次尝试都要抓一帧，一次仅
    // 对齐就要四到七帧，纯分配开销相当可观。surface 只在视口尺寸变化时重建；
    // 尺寸相同时 CopyFromScreen 直接写同一块 Bitmap（GDI 内部是 BitBlt）。
    // ToMat 仍每次新建 Mat 拷贝像素——CapturedGameFrame 的生命周期独立于
    // surface，绝不能共享内存。
    private readonly object _captureSurfaceGate = new();
    private Bitmap? _captureSurface;
    private Graphics? _captureGraphics;

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

    private bool TryCapture(
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
            // surface 复用于就绪轮询等连续抓帧：同一视口尺寸下只抓不分配。
            lock (_captureSurfaceGate)
            {
                var surface = GetOrCreateCaptureSurface(
                    (int)Math.Round(viewportBounds.Width),
                    (int)Math.Round(viewportBounds.Height));
                _captureGraphics!.CopyFromScreen(
                    (int)Math.Round(viewportBounds.X),
                    (int)Math.Round(viewportBounds.Y),
                    0,
                    0,
                    surface.Size,
                    CopyPixelOperation.SourceCopy);
                if (GetForegroundWindow() != window)
                {
                    failureReason = "捕获期间前台窗口发生变化，已丢弃这一帧，请保持游戏地图打开后重试。";
                    return false;
                }
                // ToMat 每次新建 Mat 并拷贝像素，CapturedGameFrame 持有它，与
                // surface 复用无关；surface 下一帧会被覆盖，Mat 不受影响。
                frame = new CapturedGameFrame(
                    BitmapConverter.ToMat(surface),
                    clientBounds,
                    viewportBounds,
                    window);
                return true;
            }
        }
        catch (Exception exception)
        {
            failureReason = $"无法捕获 dwrg.exe 游戏画面：{exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// 获取可复用的抓屏 surface。视口尺寸未变时直接返回缓存的 Bitmap（及配套
    /// Graphics）；尺寸变化（窗口缩放/校准区域调整）时释放旧 surface 重建。
    /// 调用方必须持有 <see cref="_captureSurfaceGate"/>。
    /// </summary>
    private Bitmap GetOrCreateCaptureSurface(int width, int height)
    {
        if (_captureSurface is { } cached
            && cached.Width == width
            && cached.Height == height)
        {
            return cached;
        }

        _captureGraphics?.Dispose();
        _captureGraphics = null;
        _captureSurface?.Dispose();
        _captureSurface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _captureGraphics = Graphics.FromImage(_captureSurface);
        return _captureSurface;
    }

    // Process.GetProcessById 会走一次系统进程表查询，实测是每次抓帧里除位块
    // 传输外最贵的一步；而就绪轮询与稳定帧循环每次尝试都要抓一帧，一次仅对齐
    // 就要问上四到七遍同一个前台窗口。窗口句柄与进程 id 同时未变时结论不可能
    // 变化，缓存上一次的判定即可；两者同时被回收复用的概率可以忽略，且最坏
    // 后果只是对一个非游戏窗口截了一帧——那一帧随后会被前台窗口复核和识别
    // 管线拒掉。
    private static readonly object ForegroundVerdictGate = new();
    private static IntPtr _verdictWindow;
    private static uint _verdictProcessId;
    private static bool _verdictIsGameWindow;
    private static bool _hasVerdict;

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
        lock (ForegroundVerdictGate)
        {
            if (_hasVerdict
                && _verdictWindow == window
                && _verdictProcessId == processId)
            {
                if (_verdictIsGameWindow)
                    return true;
                failureReason = "dwrg.exe 不是前台窗口，请返回游戏并打开完整地图。";
                return false;
            }
        }

        bool isGameWindow;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            isGameWindow = string.Equals(
                process.ProcessName,
                ProcessName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 进程已退出或无权访问：不缓存结论，下次重新判定。
            lock (ForegroundVerdictGate)
                _hasVerdict = false;
            failureReason = "无法确认前台游戏进程，请返回游戏后重试。";
            return false;
        }

        lock (ForegroundVerdictGate)
        {
            _verdictWindow = window;
            _verdictProcessId = processId;
            _verdictIsGameWindow = isGameWindow;
            _hasVerdict = true;
        }
        if (isGameWindow)
            return true;
        failureReason = "dwrg.exe 不是前台窗口，请返回游戏并打开完整地图。";
        return false;
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
/*
 * 文件职责：DwrGameWindowCaptureService。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
