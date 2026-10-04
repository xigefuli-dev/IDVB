using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>Mouse-interactive windows that leave the game's keyboard focus intact.</summary>
internal static class GameInputPreservingWindow
{
    private static readonly SubclassProcedure Procedure = WindowProcedure;
    private const nuint SubclassId = 0x49445642;

    internal static void Apply(IntPtr window)
    {
        _ = SetWindowLong(window, -20, GetWindowLong(window, -20) | 0x08000000);
        if (!SetWindowSubclass(window, Procedure, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void Show(IntPtr window) => SetVisibility(window, true);
    internal static void Hide(IntPtr window) => SetVisibility(window, false);

    private static void SetVisibility(IntPtr window, bool visible)
    {
        if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | 0x0010 | (visible ? 0x0040u : 0x0080u)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, nuint id, nuint data)
    {
        // MA_NOACTIVATE retains the mouse click; MA_NOACTIVATEANDEAT would discard it.
        if (message == 0x0021)
            return new IntPtr(3);
        if (message == 0x0082)
            _ = RemoveWindowSubclass(window, Procedure, SubclassId);
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProcedure(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
