using System.Runtime.InteropServices;

namespace IDVBuff.Plugins.CustomPhrases;

internal static class NativeClipboard
{
    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;
    private const int ClipboardRetryCount = 10;

    public static bool TrySetText(string text, out string failureReason)
    {
        failureReason = string.Empty;
        var data = IntPtr.Zero;
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        failureReason = "无法清空系统剪贴板。";
                        return false;
                    }
                    var bytes = checked((text.Length + 1) * sizeof(char));
                    data = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                    if (data == IntPtr.Zero)
                    {
                        failureReason = "无法分配剪贴板文本内存。";
                        return false;
                    }
                    var locked = GlobalLock(data);
                    if (locked == IntPtr.Zero)
                    {
                        failureReason = "无法锁定剪贴板文本内存。";
                        return false;
                    }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                        Marshal.WriteInt16(locked, text.Length * sizeof(char), 0);
                    }
                    finally
                    {
                        GlobalUnlock(data);
                    }
                    if (SetClipboardData(CfUnicodeText, data) == IntPtr.Zero)
                    {
                        failureReason = "无法写入系统剪贴板。";
                        return false;
                    }
                    data = IntPtr.Zero;
                    return true;
                }
                finally
                {
                    if (data != IntPtr.Zero)
                        GlobalFree(data);
                    CloseClipboard();
                }
            }
            Thread.Sleep(15);
        }

        failureReason = "系统剪贴板正被其他程序占用，请稍后重试。";
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
