using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace IDVBuff.Lifecycle;

/// <summary>Shows the real main HWND and completes its requested foreground handoff.</summary>
internal static class MainWindowPresentation
{
    internal static void Show(Window window, bool bringToForeground, MainWindowPlacement? placement = null)
    {
        var handle = WindowNative.GetWindowHandle(window);
        ShowWindow(handle, bringToForeground ? 5 : 8);
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            // OverlappedPresenter.Restore returns to normal state. Reapply the
            // remembered non-minimized placement to retain a maximized window.
            if (placement is null || !placement.Apply(handle, activate: bringToForeground))
                presenter.Restore(bringToForeground);
        }
        if (!bringToForeground)
            return;

        window.Activate();
        var foreground = GetForegroundWindow();
        if (foreground == handle)
            return;
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            var attached = AttachThreadInput(currentThread, foregroundThread, true);
            try { BringWindowToTop(handle); SetForegroundWindow(handle); }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        }
        else
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
