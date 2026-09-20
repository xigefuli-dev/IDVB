using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.UI;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Transparent compositor backdrop retained for the interactive WinUI manual-selection windows.
/// The non-interactive runtime overlay uses <see cref="MapOverlayNativeWindow"/> instead.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private const uint WmEraseBackground = 0x0014;
    private const uint WmDwmCompositionChanged = 0x031E;
    private const uint DwmBlurEnable = 0x00000001;
    private const uint DwmBlurRegion = 0x00000002;
    private const uint DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerDoNotRound = 1;
    private const nuint SubclassId = 0x49445642;
    private static readonly Lazy<Windows.UI.Composition.Compositor> SharedCompositor = new(() =>
    {
        WindowsCompositionDispatcherQueue.Ensure();
        return new Windows.UI.Composition.Compositor();
    });

    private readonly SubclassProcedure _subclassProcedure;
    private Windows.UI.Composition.CompositionBrush? _brush;
    private IntPtr _windowHandle;
    private IntPtr _backgroundBrush;

    internal TransparentBackdrop()
    {
        _subclassProcedure = WindowSubclassProcedure;
    }

    protected override void OnTargetConnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _brush = SharedCompositor.Value.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }

    internal void AttachWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("透明图层窗口句柄无效。", nameof(windowHandle));
        if (_windowHandle == windowHandle)
            return;
        if (_windowHandle != IntPtr.Zero)
            throw new InvalidOperationException("透明背景不能同时附加到多个窗口。");

        _windowHandle = windowHandle;
        try
        {
            ConfigureDwm(windowHandle);
            _backgroundBrush = CreateSolidBrush(0);
            if (_backgroundBrush == IntPtr.Zero)
                throw new InvalidOperationException("无法创建透明图层背景画刷。");

            SetLastError(0);
            if (!SetWindowSubclass(
                    windowHandle,
                    _subclassProcedure,
                    SubclassId,
                    UIntPtr.Zero))
            {
                throw new InvalidOperationException(
                    $"无法安装透明图层窗口过程（Win32 {Marshal.GetLastWin32Error()}）。");
            }
            ClearBackground(windowHandle, IntPtr.Zero);
        }
        catch
        {
            DetachWindow();
            throw;
        }
    }

    internal void DetachWindow()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            _windowHandle = IntPtr.Zero;
        }
        if (_backgroundBrush != IntPtr.Zero)
        {
            DeleteObject(_backgroundBrush);
            _backgroundBrush = IntPtr.Zero;
        }
    }

    protected override void OnTargetDisconnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        DetachWindow();
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private IntPtr WindowSubclassProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        UIntPtr referenceData)
    {
        if (message == WmEraseBackground && ClearBackground(window, wParam))
            return new IntPtr(1);
        if (message == WmDwmCompositionChanged)
        {
            try
            {
                ConfigureDwm(window);
            }
            catch
            {
                // Never let a managed exception cross the native window-procedure boundary.
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private bool ClearBackground(IntPtr window, IntPtr deviceContext)
    {
        if (_backgroundBrush == IntPtr.Zero
            || !GetClientRect(window, out var clientRect))
        {
            return false;
        }

        var ownsDeviceContext = deviceContext == IntPtr.Zero;
        if (ownsDeviceContext)
            deviceContext = GetDC(window);
        if (deviceContext == IntPtr.Zero)
            return false;
        try
        {
            return FillRect(deviceContext, ref clientRect, _backgroundBrush) != 0;
        }
        finally
        {
            if (ownsDeviceContext)
                ReleaseDC(window, deviceContext);
        }
    }

    private static void ConfigureDwm(IntPtr window)
    {
        var margins = new DwmMargins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(window, ref margins));

        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero)
            throw new InvalidOperationException("无法创建透明图层 DWM 区域。");
        try
        {
            var blurBehind = new DwmBlurBehind
            {
                Flags = DwmBlurEnable | DwmBlurRegion,
                Enable = true,
                BlurRegion = region
            };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(window, ref blurBehind));
        }
        finally
        {
            DeleteObject(region);
        }

        var cornerPreference = DwmWindowCornerDoNotRound;
        _ = DwmSetWindowAttribute(
            window,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        UIntPtr referenceData);

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        internal int Left;
        internal int Right;
        internal int Top;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        internal uint Flags;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool Enable;
        internal IntPtr BlurRegion;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr window,
        SubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        IntPtr window,
        ref DwmMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(
        IntPtr window,
        ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        uint attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect clientRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(
        IntPtr deviceContext,
        ref NativeRect rectangle,
        IntPtr brush);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);
}

internal static class WindowsCompositionDispatcherQueue
{
    private static object? _controller;

    internal static void Ensure()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null
            || _controller is not null)
        {
            return;
        }

        object controller = null!;
        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,
            ApartmentType = 2
        };
        var result = CreateDispatcherQueueController(options, ref controller);
        Marshal.ThrowExceptionForHR(result);
        _controller = controller;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int Size;
        internal int ThreadType;
        internal int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object dispatcherQueueController);
}
/*
 * 文件职责：WinUiTransparentBackdrop。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
