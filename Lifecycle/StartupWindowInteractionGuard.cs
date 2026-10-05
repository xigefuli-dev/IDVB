using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IDVBuff.Lifecycle;

/// <summary>
/// Prevents an incomplete WinUI HWND from accepting input or activation while
/// still allowing it to be shown for Loaded/rendering and placement restoration.
/// </summary>
internal sealed class StartupWindowInteractionGuard : IDisposable
{
    private const int ExStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private readonly IntPtr _window;
    private readonly bool _wasEnabled;
    private readonly bool _hadNoActivate;
    private bool _disposed;

    internal StartupWindowInteractionGuard(IntPtr window)
    {
        if (!IsWindow(window))
            throw new ArgumentException("A live startup window is required.", nameof(window));
        _window = window;
        _wasEnabled = IsWindowEnabled(window);
        var style = GetWindowLong(window, ExStyleIndex);
        _hadNoActivate = (style & NoActivate) != 0;
        try
        {
            SetExtendedStyle(style | NoActivate);
            EnableWindow(window, false);
            if (IsWindowEnabled(window))
                throw new Win32Exception("The startup window could not be disabled.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!IsWindow(_window))
            return;
        try
        {
            // Preserve unrelated style changes made by WinUI/theme registration.
            var style = GetWindowLong(_window, ExStyleIndex);
            SetExtendedStyle(_hadNoActivate ? style | NoActivate : style & ~NoActivate);
        }
        finally
        {
            EnableWindow(_window, _wasEnabled);
        }
    }

    private void SetExtendedStyle(int style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLong(_window, ExStyleIndex, style);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
