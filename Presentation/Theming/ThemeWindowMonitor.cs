using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace IDVBuff.Presentation.Theming;

/// <summary>Desktop theme notifications; AccessibilitySettings events require a CoreWindow.</summary>
internal sealed class ThemeWindowMonitor : IDisposable
{
    private readonly nint _handle;
    private readonly SubclassProc _callback;
    private readonly nuint _id;
    private static long _nextId;
    private bool _disposed;

    public ThemeWindowMonitor(Window window)
    {
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _id = (nuint)Interlocked.Increment(ref _nextId);
        _callback = OnMessage;
        if (!SetWindowSubclass(_handle, _callback, _id, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法订阅窗口主题消息。");
    }

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message is 0x001A or 0x031A) // WM_SETTINGCHANGE, WM_THEMECHANGED
            ThemeService.QueueSystemRefresh();
        if (message == 0x0082) Dispose(); // WM_NCDESTROY
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_handle, _callback, _id);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
