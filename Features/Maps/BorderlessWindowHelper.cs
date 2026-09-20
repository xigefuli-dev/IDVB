using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 解决 Windows App SDK 1.6+ 针对无边框/非调整大小窗口强制绘制白色边框与标题栏的系统 Bug (#9978, #9621)。
/// </summary>
internal static class BorderlessWindowHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int GWL_STYLE = -16;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_CAPTION = 0x00C00000;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMWCP_ROUND = 2;

    public static void Apply(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        // 1. 彻底剔除系统边框与标题栏样式，解决 WASDK 强加的白色高亮线 (#9978, #9621)
        var style = GetWindowLong(hWnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME | WS_CAPTION);
        SetWindowLong(hWnd, GWL_STYLE, style);

        // 2. 强制触发非客户区重新计算
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // 3. 配置 DWM 深色沉浸模式、关闭 DWM 边框与标题栏绘制色
        int darkMode = 1;
        DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
        DwmSetWindowAttribute(hWnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
        int cornerRound = DWMWCP_ROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerRound, sizeof(int));
    }
}
