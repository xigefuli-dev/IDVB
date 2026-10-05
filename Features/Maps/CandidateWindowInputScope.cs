using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>Normal foreground input handoff for the modal map chooser.</summary>
internal sealed class CandidateWindowInputScope : IDisposable
{
    private const nuint SubclassId = 0x49445643;
    private readonly IntPtr _window;
    private readonly IntPtr _game;
    private readonly SubclassProcedure _procedure;
    private bool _installed;
    private bool _disposed;

    internal CandidateWindowInputScope(IntPtr window, IntPtr game)
    {
        _window = window;
        _game = game;
        _procedure = WindowProcedure;
        if (!SetWindowSubclass(window, _procedure, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _installed = true;
    }

    internal bool OwnsForeground => OwnsWindow(GetForegroundWindow());

    internal void Show()
    {
        // A scan may finish after the player has switched to another app.
        // Display the chooser without stealing that app's focus.
        var gameWasForeground = _game != IntPtr.Zero && GetForegroundWindow() == _game;
        if (!SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0040))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (gameWasForeground && GetForegroundWindow() == _game)
            _ = SetForegroundWindow(_window);
        ReleaseCursorIfForeground();
    }

    private bool OwnsWindow(IntPtr window) => window != IntPtr.Zero
        && (window == _window || GetAncestor(window, 3) == _window); // GA_ROOTOWNER, including flyouts.

    private void ReleaseCursorIfForeground()
    {
        if (OwnsForeground)
            _ = ClipCursor(IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_installed)
        {
            _ = RemoveWindowSubclass(_window, _procedure, SubclassId);
            _installed = false;
        }
        // Run before destroying the chooser, while ownership is still observable.
        // Normal activation delivers focus-loss/gain messages to both apps. Never
        // inject key releases/re-presses or restore a stale cursor confinement.
        if (!OwnsForeground) return;
        if (OwnsWindow(GetCapture())) _ = ReleaseCapture();
        if (_game != IntPtr.Zero && IsWindow(_game))
            _ = SetForegroundWindow(_game);
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0006 && (wParam.ToInt64() & 0xffff) != 0) // WM_ACTIVATE
            ReleaseCursorIfForeground();
        if (message == 0x0082) // WM_NCDESTROY
        {
            _ = RemoveWindowSubclass(window, _procedure, SubclassId);
            _installed = false;
        }
        return result;
    }

    private delegate IntPtr SubclassProcedure(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
