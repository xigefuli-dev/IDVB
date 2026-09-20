using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;
using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 游戏加加风格顶部实时性能监控小横条。
/// 采用无焦点、点击穿透的 Win32 Layered Window 置顶显示当前 RAM、GC 堆、增量与正在调用的关键函数。
/// </summary>
internal sealed class RealtimePerformanceOverlay : IDisposable
{
    private const int BarWidth = 736;
    private const int BarHeight = 37;
    private const int RefreshIntervalMs = 350;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x8000000;
    private const uint UlwAlpha = 2;
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SmCxScreen = 0;
    private static readonly IntPtr HwndTopMost = new(-1);

    private static readonly object WindowClassGate = new();
    private static readonly WindowProcedure WindowProcedureDelegate = WindowProcedureCore;
    private static bool _windowClassRegistered;

    private readonly object _gate = new();
    private readonly ICaptureProtectionService? _captureProtection;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;
    private IntPtr _window;
    private Timer? _refreshTimer;
    private bool _disposed;
    private bool _isEnabled;
    private MapScreenRect _gameBounds;

    public static RealtimePerformanceOverlay? Instance { get; private set; }

    public RealtimePerformanceOverlay(ICaptureProtectionService? captureProtection = null)
    {
        _captureProtection = captureProtection;
        Instance = this;
    }

    public static void SetEnabled(bool enabled, ICaptureProtectionService? captureProtection = null)
    {
        if (Instance is null)
        {
            if (!enabled) return;
            Instance = new RealtimePerformanceOverlay(captureProtection);
        }
        Instance.IsEnabled = enabled;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            lock (_gate)
            {
                if (_disposed || _isEnabled == value) return;
                _isEnabled = value;
                if (value)
                {
                    StartTimer();
                    Paint();
                }
                else
                {
                    StopTimer();
                    Hide();
                }
            }
        }
    }

    public void UpdateGameBounds(MapScreenRect bounds)
    {
        lock (_gate)
        {
            _gameBounds = bounds;
        }
        if (_isEnabled)
            Paint();
    }

    public void Show() => IsEnabled = true;

    public void Hide()
    {
        lock (_gate)
        {
            if (_window != IntPtr.Zero)
                ShowWindow(_window, 0);
        }
    }

    private void StartTimer()
    {
        _refreshTimer ??= new Timer(
            static state =>
            {
                var overlay = (RealtimePerformanceOverlay)state!;
                overlay.OnTimerTick();
            },
            this,
            RefreshIntervalMs,
            RefreshIntervalMs);
    }

    private void StopTimer()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    private void OnTimerTick()
    {
        if (!_isEnabled || _disposed) return;
        try
        {
            Paint();
        }
        catch
        {
            // 忽略偶发的绘制竞争
        }
    }

    private void Paint()
    {
        if (_disposed || !_isEnabled) return;

        MapScreenRect bounds;
        lock (_gate)
        {
            bounds = _gameBounds;
        }

        EnsureWindow();
        var snap = RealtimePerformanceTracker.CaptureSnapshot();

        using var bitmap = new Bitmap(BarWidth, BarHeight, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 1. 半透明暗黑磨砂背景
            using var backBrush = new SolidBrush(Color.FromArgb(225, 20, 22, 28));
            using var borderPen = new Pen(Color.FromArgb(120, 80, 95, 120), 1f);
            using var backPath = Round(new RectangleF(0.5f, 0.5f, BarWidth - 1f, BarHeight - 1f), 9.2f);
            g.FillPath(backBrush, backPath);
            g.DrawPath(borderPen, backPath);

            // 2. 状态指示光点（按独占专用物理内存划分阈值）
            var dotColor = snap.WorkingSetMb switch
            {
                < 250 => Color.FromArgb(255, 0, 230, 118),
                < 380 => Color.FromArgb(255, 255, 214, 0),
                _ => Color.FromArgb(255, 255, 82, 82)
            };
            using var dotBrush = new SolidBrush(dotColor);
            g.FillEllipse(dotBrush, 14f, (BarHeight - 8f) / 2f, 8f, 8f);

            // 3. 字体与布局
            using var labelFont = new Font("Segoe UI", 13.2f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var boldFont = new Font("Segoe UI", 13.8f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var subFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var labelBrush = new SolidBrush(Color.FromArgb(255, 150, 155, 165));
            using var divBrush = new SolidBrush(Color.FromArgb(255, 60, 65, 75));
            using var cyanBrush = new SolidBrush(Color.FromArgb(255, 64, 196, 255));
            using var goldBrush = new SolidBrush(Color.FromArgb(255, 255, 213, 79));
            using var limeBrush = new SolidBrush(Color.FromArgb(255, 178, 255, 89));
            using var subBrush = new SolidBrush(Color.FromArgb(180, 140, 145, 155));

            float x = 31f;
            float y = (BarHeight - 18.4f) / 2f;

            // RAM（主数值为活动专用工作集，精确吻合任务管理器“进程”选项卡中的“内存”列）
            g.DrawString("RAM", labelFont, labelBrush, x, y);
            x += g.MeasureString("RAM", labelFont).Width + 2.3f;
            using var ramValBrush = new SolidBrush(dotColor);
            var ramStr = $"{snap.WorkingSetMb:F0} MB";
            g.DrawString(ramStr, boldFont, ramValBrush, x, y - 0.6f);
            x += g.MeasureString(ramStr, boldFont).Width + 3.5f;

            // 附带显示总工作集（含系统共享页等，让开发者同时洞察两者）
            if (snap.TotalWorkingSetMb > snap.WorkingSetMb + 20)
            {
                var totalStr = $"({snap.TotalWorkingSetMb:F0}M)";
                g.DrawString(totalStr, subFont, subBrush, x, y + 0.9f);
                x += g.MeasureString(totalStr, subFont).Width + 9.2f;
            }
            else
            {
                x += 7f;
            }

            // 分隔符
            g.DrawString("|", labelFont, divBrush, x, y);
            x += 11.5f;

            // GC
            g.DrawString("GC", labelFont, labelBrush, x, y);
            x += g.MeasureString("GC", labelFont).Width + 2.3f;
            var gcStr = $"{snap.GcHeapMb:F0} MB";
            g.DrawString(gcStr, boldFont, cyanBrush, x, y - 0.6f);
            x += g.MeasureString(gcStr, boldFont).Width + 9.2f;

            // 分隔符
            g.DrawString("|", labelFont, divBrush, x, y);
            x += 11.5f;

            // Delta
            g.DrawString("Δ", labelFont, labelBrush, x, y);
            x += g.MeasureString("Δ", labelFont).Width + 2.3f;
            var delta = snap.LastDeltaWorkingSetMb;
            var deltaSign = delta > 0 ? "+" : "";
            var deltaStr = $"{deltaSign}{delta:F1}M";
            var deltaColor = Math.Abs(delta) < 0.2
                ? Color.FromArgb(255, 160, 160, 160)
                : (delta > 0 ? Color.FromArgb(255, 255, 82, 82) : Color.FromArgb(255, 0, 230, 118));
            using var deltaBrush = new SolidBrush(deltaColor);
            g.DrawString(deltaStr, boldFont, deltaBrush, x, y - 0.6f);
            x += g.MeasureString(deltaStr, boldFont).Width + 9.2f;

            // 分隔符
            g.DrawString("|", labelFont, divBrush, x, y);
            x += 11.5f;

            // 当前热点函数与耗时
            var funcName = string.IsNullOrEmpty(snap.ActiveFunction) ? "Idle" : snap.ActiveFunction;
            if (funcName.Length > 28)
                funcName = funcName[..26] + "…";
            var funcStr = $"[{funcName}]";
            g.DrawString(funcStr, boldFont, goldBrush, x, y - 0.6f);
            x += g.MeasureString(funcStr, boldFont).Width + 7f;

            if (snap.ActiveElapsedMs > 0.5)
            {
                var timeStr = $"{snap.ActiveElapsedMs:F0}ms";
                g.DrawString(timeStr, labelFont, limeBrush, x, y);
            }
        }

        // 计算居中位置（优先跟随游戏窗口顶部居中，若未获取则居中屏幕顶部）
        int targetX, targetY;
        if (bounds.IsValid && bounds.Width > 200)
        {
            targetX = (int)Math.Round(bounds.X + (bounds.Width - BarWidth) / 2d);
            targetY = (int)Math.Round(bounds.Y + 8d);
        }
        else
        {
            var screenWidth = GetSystemMetrics(SmCxScreen);
            targetX = Math.Max(0, (screenWidth - BarWidth) / 2);
            targetY = 10;
        }

        Present(bitmap, targetX, targetY);
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
                    ClassName = "IDVBuff.RealtimePerformanceOverlay"
                };
                RegisterClassEx(ref windowClass);
                _windowClassRegistered = true;
            }
        }
        _window = CreateWindowEx(
            WsExLayered | WsExTransparent | WsExNoActivate,
            "IDVBuff.RealtimePerformanceOverlay",
            "",
            WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
            throw new InvalidOperationException("无法创建实时性能监控窗口。");

        if (_captureProtection is not null)
        {
            try
            {
                _captureProtectionRegistration = _captureProtection.RegisterWindow(
                    _window,
                    CaptureProtectionWindowCategory.DisplayLayer,
                    "实时性能监控");
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[RealtimePerformanceOverlay] 捕获保护登记失败：{exception.Message}");
            }
        }
    }

    private void Present(Bitmap bitmap, int x, int y)
    {
        ShowWindow(_window, 4);
        SetWindowPos(_window, HwndTopMost, x, y, bitmap.Width, bitmap.Height, SwpNoActivate | SwpShowWindow);
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var handle = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, handle);
        try
        {
            var point = new PointNative(x, y);
            var size = new SizeNative(bitmap.Width, bitmap.Height);
            var source = new PointNative(0, 0);
            var blend = new Blend { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(_window, screen, ref point, ref size, memory, ref source, 0, ref blend, UlwAlpha))
                throw new InvalidOperationException("UpdateLayeredWindow 失败。");
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(handle);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static IntPtr WindowProcedureCore(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        return message == WmNcHitTest ? new IntPtr(HtTransparent) : DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
        _captureProtectionRegistration?.Dispose();
        _captureProtectionRegistration = null;
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative(int x, int y) { public int X = x, Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative(int x, int y) { public int X = x, Y = y; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Blend { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref PointNative point, ref SizeNative size, IntPtr memory, ref PointNative source, uint colorKey, ref Blend blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
