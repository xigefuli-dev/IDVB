using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapOverlayNativeWindow : IDisposable
{
    private const string WindowClassName = "IDVBuff.MapOverlay.NativeWindow";
    private const uint WsPopup = 0x80000000;
    private const uint WmNchittest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const byte AcSrcOver = 0;
    internal const byte AcSrcAlpha = 1;
    internal const uint UlwAlpha = 0x00000002;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    internal const uint MonitorDefaultToNearest = 2;
    internal static readonly IntPtr HwndTopMost = new(-1);
    private static readonly object ClassRegistrationGate = new();
    private static readonly WindowProcedureDelegate WindowProcedure = WindowProcedureCore;
    private static bool _classRegistered;

    internal IntPtr _handle;
    internal bool _disposed;
    private readonly ICaptureProtectionService? _captureProtection;
    private ICaptureProtectionRegistration? _captureProtectionRegistration;
    private bool _captureExcluded;

    internal MapOverlayNativeWindow(ICaptureProtectionService? captureProtection = null)
    {
        _captureProtection = captureProtection;
    }

    internal IntPtr Handle => _handle;
    internal bool IsVisible { get; set; }

    internal void Hide()
    {
        if (_handle != IntPtr.Zero)
            ShowWindow(_handle, SwHide);
        IsVisible = false;
    }

    private void EnsureWindow()
    {
        if (_handle != IntPtr.Zero)
            return;
        EnsureWindowClass();

        SetLastError(0);
        _handle = CreateWindowEx(
            (uint)MapOverlayWindowStyles.Create(),
            WindowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (_handle == IntPtr.Zero)
            throw NativeFailure("Unable to create the native overlay window.");

        var appliedStyles = GetWindowLongPtr(_handle, MapOverlayWindowStyles.GwlExStyle).ToInt64();
        if (!MapOverlayWindowStyles.AreApplied(appliedStyles))
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException("The native overlay window did not retain its required input styles.");
        }
        if (_captureProtection is not null)
        {
            try
            {
                _captureProtectionRegistration = _captureProtection.RegisterWindow(
                    _handle,
                    CaptureProtectionWindowCategory.DisplayLayer,
                    "地图/状态 Overlay");
                _captureExcluded = _captureProtectionRegistration.IsProtectionApplied;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Overlay] 捕获保护登记失败：{exception.Message}");
            }
        }
        Hide();
    }

    internal bool IsCaptureExclusionEnabled => _captureExcluded;

    internal bool TrySetCaptureExclusion(bool enabled, out string failureReason)
    {
        failureReason = string.Empty;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWindow();
            SetLastError(0);
            if (SetWindowDisplayAffinity(
                    _handle,
                    enabled ? WdaExcludeFromCapture : WdaNone))
            {
                _captureExcluded = enabled;
                return true;
            }
            var error = Marshal.GetLastWin32Error();
            failureReason = $"SetWindowDisplayAffinity failed (Win32 {error}).";
            _captureExcluded = false;
            return false;
        }
        catch (Exception exception)
        {
            failureReason = exception.Message;
            return false;
        }
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered)
            return;
        lock (ClassRegistrationGate)
        {
            if (_classRegistered)
                return;

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Instance = GetModuleHandle(null),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                ClassName = WindowClassName
            };
            SetLastError(0);
            var atom = RegisterClassEx(ref windowClass);
            var error = Marshal.GetLastWin32Error();
            if (atom == 0 && error != ErrorClassAlreadyExists)
                throw new InvalidOperationException($"Unable to register the overlay window class (Win32 {error}).");
            _classRegistered = true;
        }
    }

    private static IntPtr WindowProcedureCore(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmNchittest)
            return new IntPtr(HtTransparent);
        if (message == WmMouseActivate)
            return new IntPtr(MaNoActivate);
        return DefWindowProc(window, message, wParam, lParam);
    }

    internal static InvalidOperationException NativeFailure(string message)
    {
        var err = Marshal.GetLastWin32Error();
        var errMsg = err != 0 ? $" (Win32 错误码: {err})" : "";
        Debug.WriteLine($"[Overlay] NativeFailure: {message}{errMsg}");
        return new($"{message}{errMsg}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Hide();
        CleanupCachedBuffer();
        _captureProtectionRegistration?.Dispose();
        _captureProtectionRegistration = null;
        _captureExcluded = false;
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string ClassName;
        internal IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);
}
/*
 * 文件职责：MapOverlayNativeWindow。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
