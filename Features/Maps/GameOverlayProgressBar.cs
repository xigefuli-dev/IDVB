using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 可复用的游戏内进度条。它使用独立、点击穿透的小型分层窗口，而不是现有的全屏地图 Overlay。
/// 信息文本与百分比文本分别绘制，调用方可用 <see cref="Show"/>、<see cref="Report"/> 和
/// <see cref="Complete"/> 驱动任何耗时操作的进度反馈。
/// </summary>
internal sealed class GameOverlayProgressBar : IDisposable
{
    private const int BarWidth = 340, BarHeight = 56, FrameMs = 16;
    private const int WsPopup = unchecked((int)0x80000000), WsExLayered = 0x80000, WsExTransparent = 0x20, WsExNoActivate = 0x8000000;
    private const uint UlwAlpha = 2;
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);
    private readonly object _gate = new();
    private readonly ICaptureProtectionService? _captureProtection;
    private static readonly object WindowClassGate = new();
    private static readonly WindowProcedure WindowProcedureDelegate = WindowProcedureCore;
    private static bool _windowClassRegistered;
    private IntPtr _window;
    private bool _disposed, _completing;
    private long _version;
    private MapScreenRect _bounds;
    private string _text = "正在扫描...";
    private double _progress;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;

    public GameOverlayProgressBar(ICaptureProtectionService? captureProtection = null)
    {
        _captureProtection = captureProtection;
    }

    /// <summary>显示进度条并从游戏画面下方平滑进入至 -70% 位置。</summary>
    public void Show(MapScreenRect bounds, IntPtr gameWindowHandle, string text)
    {
        if (!bounds.IsValid || gameWindowHandle == IntPtr.Zero || _disposed) return;
        long version;
        lock (_gate) { _bounds = bounds; _text = text; _progress = 0; _completing = false; version = ++_version; }
        _ = AnimateAsync(version, completing: false);
    }

    /// <summary>更新当前进度；文本为空时保留上一次的信息文本。</summary>
    public void Report(double progress, string? text = null)
    {
        lock (_gate)
        {
            if (_disposed || _version == 0 || _completing) return;
            _progress = Math.Clamp(progress, 0, 1);
            if (!string.IsNullOrWhiteSpace(text)) _text = text;
        }
    }

    /// <summary>显示“完成”，0.7 秒变为 #00BA1C，停留 1.8 秒后按入场反向退出。</summary>
    public void Complete()
    {
        long version;
        lock (_gate)
        {
            if (_disposed || _version == 0 || _completing) return;
            _progress = 1; _text = "完成"; _completing = true; version = _version;
        }
        _ = AnimateAsync(version, completing: true);
    }

    /// <summary>立即关闭，用于对局结束和宿主释放。</summary>
    public void Hide() { lock (_gate) { ++_version; _completing = false; } if (_window != IntPtr.Zero) ShowWindow(_window, 0); }

    private async Task AnimateAsync(long version, bool completing)
    {
        try
        {
            if (!completing)
            {
                await PhaseAsync(version, 0, 1, 0.42, 0);
                while (Current(version, out var isCompleting) && !isCompleting) { Paint(1, 0); await Task.Delay(FrameMs); }
                if (!Current(version, out _)) return;
            }
            await PhaseAsync(version, 1, 1, 0.7, 1);
            await HoldAsync(version, 1.8, 1);
            await PhaseAsync(version, 1, 0, 0.42, 1);
            if (Current(version, out _)) Hide();
        }
        catch { Hide(); }
    }

    private async Task PhaseAsync(long version, double from, double to, double seconds, double green)
    {
        var start = Environment.TickCount64;
        while (Current(version, out _))
        {
            var raw = Math.Clamp((Environment.TickCount64 - start) / (seconds * 1000d), 0, 1);
            var eased = raw < .5 ? 4 * raw * raw * raw : 1 - Math.Pow(-2 * raw + 2, 3) / 2;
            Paint(from + (to - from) * eased, green == 0 ? 0 : eased);
            if (raw >= 1) return;
            await Task.Delay(FrameMs);
        }
    }

    private async Task HoldAsync(long version, double seconds, double green)
    {
        var until = Environment.TickCount64 + (long)(seconds * 1000);
        while (Environment.TickCount64 < until && Current(version, out _)) { Paint(1, green); await Task.Delay(FrameMs); }
    }

    private bool Current(long version, out bool completing) { lock (_gate) { completing = _completing; return !_disposed && _version == version; } }

    private void Paint(double visibility, double green)
    {
        MapScreenRect bounds; string text; double progress;
        lock (_gate) { bounds = _bounds; text = _text; progress = _progress; }
        EnsureWindow();
        using var bitmap = new Bitmap(BarWidth, BarHeight, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var back = new SolidBrush(Color.FromArgb(235, 55, 82, 112)); using var backPath = Round(new RectangleF(0, 0, BarWidth - 1, BarHeight - 1), BarHeight / 2f); g.FillPath(back, backPath);
            var blue = Color.FromArgb(255, 42, 130, 228); var success = Color.FromArgb(255, 0, 186, 28);
            using var fill = new SolidBrush(Color.FromArgb(255, (int)(blue.R + (success.R - blue.R) * green), (int)(blue.G + (success.G - blue.G) * green), (int)(blue.B + (success.B - blue.B) * green)));
            using var fillPath = Round(new RectangleF(0, 0, Math.Max(BarHeight, BarWidth * (float)progress), BarHeight - 1), BarHeight / 2f); g.FillPath(fill, fillPath);
            using var font = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel); using var white = new SolidBrush(Color.White);
            using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            // 信息文本层、进度文本层各自独立绘制。
            g.DrawString(text, font, white, new RectangleF(34, 0, 190, BarHeight), left);
            g.DrawString($"{progress * 100:0}%", font, white, new RectangleF(225, 0, 80, BarHeight), right);
        }
        var targetY = bounds.Y + bounds.Height * .85 - BarHeight / 2d; // -70%
        var hiddenY = bounds.Y + bounds.Height + BarHeight + 8d;
        Present(bitmap, (int)Math.Round(bounds.X + (bounds.Width - BarWidth) / 2d), (int)Math.Round(hiddenY + (targetY - hiddenY) * visibility));
    }

    private void EnsureWindow()
    {
        if (_window != IntPtr.Zero) return;
        lock (WindowClassGate)
        {
            if (!_windowClassRegistered)
            {
                var windowClass = new WindowClassEx
                {
                    Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                    Instance = GetModuleHandle(null),
                    Procedure = Marshal.GetFunctionPointerForDelegate(WindowProcedureDelegate),
                    ClassName = "IDVBuff.GameOverlayProgressBar"
                };
                RegisterClassEx(ref windowClass);
                _windowClassRegistered = true;
            }
        }
        _window = CreateWindowEx(WsExLayered | WsExTransparent | WsExNoActivate, "IDVBuff.GameOverlayProgressBar", "", WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new InvalidOperationException("无法创建扫描进度窗口。");
        if (_captureProtection is not null)
        {
            try
            {
                _captureProtectionRegistration = _captureProtection.RegisterWindow(
                    _window,
                    CaptureProtectionWindowCategory.DisplayLayer,
                    "扫描进度条");
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"[ProgressOverlay] 捕获保护登记失败：{exception.Message}");
            }
        }
    }

    private void Present(Bitmap bitmap, int x, int y)
    {
        ShowWindow(_window, 4);
        // 置顶于游戏窗口之上（与全屏 Overlay 的 SetWindowPos 一致），
        // 否则对局进行时进度条会被 dwrg.exe 前景窗口遮挡。
        SetWindowPos(_window, HwndTopMost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
        var screen = GetDC(IntPtr.Zero); var memory = CreateCompatibleDC(screen); var handle = bitmap.GetHbitmap(Color.FromArgb(0)); var old = SelectObject(memory, handle);
        try { var point = new PointNative(x, y); var size = new SizeNative(bitmap.Width, bitmap.Height); var source = new PointNative(0, 0); var blend = new Blend { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; if (!UpdateLayeredWindow(_window, screen, ref point, ref size, memory, ref source, 0, ref blend, UlwAlpha)) throw new InvalidOperationException(); }
        finally { SelectObject(memory, old); DeleteObject(handle); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
    }

    private static GraphicsPath Round(RectangleF r, float radius) { var p = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height)); p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
    /// <summary>命中测试始终透传给游戏，避免进度条抢占鼠标输入。</summary>
    private static IntPtr WindowProcedureCore(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) => message == WmNcHitTest ? new IntPtr(HtTransparent) : DefWindowProc(window, message, wParam, lParam);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureProtectionRegistration?.Dispose();
        _captureProtectionRegistration = null;
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClassEx { public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; [MarshalAs(UnmanagedType.LPWStr)] public string? Menu; [MarshalAs(UnmanagedType.LPWStr)] public string ClassName; public IntPtr SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref PointNative point, ref SizeNative size, IntPtr memory, ref PointNative source, uint colorKey, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
/*
 * 文件职责：GameOverlayProgressBar。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
