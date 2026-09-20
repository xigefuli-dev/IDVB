using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 屏幕浮层通知置顶透明窗口。
/// 运行于专职后台渲染线程，具备独立消息泵与事件唤醒机制，
/// 消除跨线程 Win32 消息死锁，支持流式入场/退场/重排动画与 60 FPS 平滑呈现。
/// </summary>
public sealed class OverlayNotificationWindow : IDisposable
{
    private const int FrameMs = 16; // ~60fps
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopMost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const uint UlwAlpha = 2;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
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
    private readonly OverlayNotificationQueue _queue;
    private readonly ICaptureProtectionService? _captureProtection;
    private readonly AutoResetEvent _wakeEvent = new(false);

    private ICaptureProtectionRegistration? _captureProtectionRegistration;
    private Thread? _renderThread;
    private IntPtr _window;
    private volatile bool _disposed;
    private bool _isWindowVisible;
    private MapScreenRect _gameBounds;

    public static OverlayNotificationWindow? Instance { get; private set; }

    public OverlayNotificationWindow(
        OverlayNotificationQueue queue,
        ICaptureProtectionService? captureProtection = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _captureProtection = captureProtection;
        Instance = this;

        _queue.StateChanged += OnQueueStateChanged;

        _renderThread = new Thread(RenderThreadLoop)
        {
            IsBackground = true,
            Name = "IDVB.OverlayNotificationWindow"
        };
        _renderThread.Start();
    }

    public void UpdateGameBounds(MapScreenRect bounds)
    {
        lock (_gate)
        {
            _gameBounds = bounds;
        }
        _wakeEvent.Set();
    }

    private void OnQueueStateChanged()
    {
        _wakeEvent.Set();
    }

    private void RenderThreadLoop()
    {
        try
        {
            EnsureWindow();

            while (!_disposed)
            {
                // 1. 显式泵送 Windows 消息队列，保证无焦点透明窗口消息正常分发，消除线程死锁
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (_disposed) break;

                // 2. 推进帧状态与文本排版测量
                var now = Environment.TickCount64;
                var metrics = GetCurrentMetrics();
                bool isAnimating;

                using (var tempBmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
                using (var gMeasure = Graphics.FromImage(tempBmp))
                {
                    isAnimating = _queue.UpdateFrame(gMeasure, now, metrics);
                }

                // 3. 渲染或隐藏
                var snapshot = _queue.GetSnapshot();
                if (snapshot.Count > 0)
                {
                    Paint(snapshot, now, metrics);
                }
                else
                {
                    Hide();
                }

                if (_disposed) break;

                // 4. 事件唤醒调度：动画中保持 60 FPS 刷新，稳态低频检查，清空后深度休眠
                if (isAnimating)
                {
                    _wakeEvent.WaitOne(FrameMs);
                }
                else if (_queue.HasVisibleItems)
                {
                    _wakeEvent.WaitOne(60);
                }
                else
                {
                    _wakeEvent.WaitOne();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OverlayNotificationWindow] 渲染线程异常：{ex}");
        }
        finally
        {
            DestroyWindowCore();
        }
    }

    /// <summary>
    /// 获取当前窗口所适用的通知缩放度量。
    /// 综合屏幕/游戏窗口物理分辨率与系统 DPI 缩放，保证大分辨率或高 DPI 下正常放大。
    /// </summary>
    public NotificationMetrics GetCurrentMetrics()
    {
        MapScreenRect bounds;
        lock (_gate)
        {
            bounds = _gameBounds;
        }

        uint dpi = 0;
        if (_window != IntPtr.Zero)
        {
            try { dpi = GetDpiForWindow(_window); } catch { }
        }
        if (dpi == 0)
        {
            try { dpi = GetDpiForSystem(); } catch { }
        }
        if (dpi == 0)
        {
            dpi = 96;
        }

        var dpiScale = dpi / 96f;

        // 分辨率适配基准：以 1080p（宽 1920）为基准
        var resScale = 1.0f;
        if (bounds.IsValid && bounds.Width > 1920)
        {
            resScale = (float)(bounds.Width / 1920.0);
        }
        else
        {
            var screenWidth = GetSystemMetrics(SmCxScreen);
            if (screenWidth > 1920)
            {
                resScale = screenWidth / 1920f;
            }
        }

        var effectiveScale = Math.Clamp(Math.Max(dpiScale, resScale), 1.0f, 3.5f);
        return new NotificationMetrics(effectiveScale);
    }

    private void Paint(List<OverlayNotificationCardState> cards, long now, NotificationMetrics metrics)
    {
        if (_disposed || _window == IntPtr.Zero) return;

        MapScreenRect bounds;
        lock (_gate)
        {
            bounds = _gameBounds;
        }

        var canvasWidth = (int)Math.Ceiling(metrics.CardWidth + 40f * metrics.Scale);
        var canvasHeight = (int)Math.Ceiling(720f * metrics.Scale);

        using var bitmap = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            OverlayNotificationRenderer.RenderQueue(g, cards, now, metrics);
        }

        // 计算屏幕中上部定位（优先匹配游戏窗口中上部，无游戏窗口则优先对齐当前前台活动窗口中上部或主屏水平居中顶栏）
        int targetX, targetY;
        if (bounds.IsValid && bounds.Width > 200)
        {
            targetX = (int)Math.Round(bounds.X + (bounds.Width - canvasWidth) / 2d);
            targetY = (int)Math.Round(bounds.Y + 12d * metrics.Scale);
        }
        else
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _window && GetWindowRect(fg, out var fgRect) && (fgRect.Right - fgRect.Left) > 300)
            {
                var fgWidth = fgRect.Right - fgRect.Left;
                targetX = Math.Max(0, (int)Math.Round(fgRect.Left + (fgWidth - canvasWidth) / 2d));
                targetY = Math.Max(0, (int)Math.Round(fgRect.Top + 12d * metrics.Scale));
            }
            else
            {
                var screenWidth = GetSystemMetrics(SmCxScreen);
                targetX = Math.Max(0, (screenWidth - canvasWidth) / 2);
                targetY = (int)Math.Round(12d * metrics.Scale);
            }
        }

        Present(bitmap, targetX, targetY);
    }

    public void Hide()
    {
        if (_isWindowVisible && _window != IntPtr.Zero)
        {
            ShowWindow(_window, SwHide);
            _isWindowVisible = false;
        }
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
                    ClassName = "IDVBuff.OverlayNotificationWindow"
                };
                RegisterClassEx(ref windowClass);
                _windowClassRegistered = true;
            }
        }

        _window = CreateWindowEx(
            WsExTopMost | WsExToolWindow | WsExLayered | WsExTransparent | WsExNoActivate,
            "IDVBuff.OverlayNotificationWindow",
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
            throw new InvalidOperationException("无法创建屏幕通知浮层窗口。");

        if (_captureProtection is not null)
        {
            try
            {
                _captureProtectionRegistration = _captureProtection.RegisterWindow(
                    _window,
                    CaptureProtectionWindowCategory.DisplayLayer,
                    "通知提示");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OverlayNotificationWindow] 捕获保护登记失败：{ex.Message}");
            }
        }
    }

    private void Present(Bitmap bitmap, int x, int y)
    {
        ShowWindow(_window, SwShowNoActivate);
        SetWindowPos(_window, HwndTopMost, x, y, bitmap.Width, bitmap.Height, SwpNoActivate | SwpShowWindow);
        _isWindowVisible = true;

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
            UpdateLayeredWindow(_window, screen, ref point, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(handle);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void DestroyWindowCore()
    {
        _captureProtectionRegistration?.Dispose();
        _captureProtectionRegistration = null;

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
            _isWindowVisible = false;
        }
    }

    private static IntPtr WindowProcedureCore(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmNcHitTest)
            return new IntPtr(HtTransparent);
        if (message == WmMouseActivate)
            return new IntPtr(MaNoActivate);
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.StateChanged -= OnQueueStateChanged;
        _wakeEvent.Set();

        _renderThread?.Join(500);
        _renderThread = null;

        _wakeEvent.Dispose();

        if (Instance == this)
            Instance = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative(int x, int y) { public int X = x; public int Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative(int width, int height) { public int Width = width; public int Height = height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Value;
        public IntPtr WParam, LParam;
        public uint Time;
        public PointNative Point;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Blend { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ExtraClassBytes, ExtraWindowBytes;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref PointNative destinationPoint, ref SizeNative size, IntPtr sourceDc, ref PointNative sourcePoint, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage msg);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectNative lpRect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? moduleName);
}
