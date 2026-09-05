using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapOverlayNativeWindow
{
    private IntPtr _cachedMemoryDc = IntPtr.Zero;
    private IntPtr _cachedDIBSection = IntPtr.Zero;
    private IntPtr _cachedBitsPtr = IntPtr.Zero;
    private IntPtr _cachedOldBitmap = IntPtr.Zero;
    private int _cachedDIBWidth;
    private int _cachedDIBHeight;

    private void EnsureCachedBuffer(int width, int height)
    {
        if (_cachedMemoryDc != IntPtr.Zero && _cachedDIBWidth == width && _cachedDIBHeight == height)
            return;

        CleanupCachedBuffer();

        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            _cachedMemoryDc = CreateCompatibleDC(screenDc);
            if (_cachedMemoryDc == IntPtr.Zero)
                throw NativeFailure("Unable to create the cached overlay memory DC.");

            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height, // 负数代表 top-down DIB，扫描行与 Bitmap.LockBits 严格一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            _cachedDIBSection = CreateDIBSection(
                _cachedMemoryDc,
                ref bmi,
                0, // DIB_RGB_COLORS
                out _cachedBitsPtr,
                IntPtr.Zero,
                0);

            if (_cachedDIBSection == IntPtr.Zero || _cachedBitsPtr == IntPtr.Zero)
                throw NativeFailure("Unable to create the cached overlay DIBSection.");

            _cachedOldBitmap = SelectObject(_cachedMemoryDc, _cachedDIBSection);
            _cachedDIBWidth = width;
            _cachedDIBHeight = height;
        }
        finally
        {
            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal void CleanupCachedBuffer()
    {
        if (_cachedOldBitmap != IntPtr.Zero && _cachedMemoryDc != IntPtr.Zero)
        {
            SelectObject(_cachedMemoryDc, _cachedOldBitmap);
            _cachedOldBitmap = IntPtr.Zero;
        }
        if (_cachedDIBSection != IntPtr.Zero)
        {
            DeleteObject(_cachedDIBSection);
            _cachedDIBSection = IntPtr.Zero;
        }
        if (_cachedMemoryDc != IntPtr.Zero)
        {
            DeleteDC(_cachedMemoryDc);
            _cachedMemoryDc = IntPtr.Zero;
        }
        _cachedBitsPtr = IntPtr.Zero;
        _cachedDIBWidth = 0;
        _cachedDIBHeight = 0;
    }

    internal void Present(Bitmap bitmap, MapScreenRect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!bounds.IsValid)
            throw new ArgumentException("Overlay bounds are invalid.", nameof(bounds));
        EnsureWindow();

        var x = (int)Math.Round(bounds.X);
        var y = (int)Math.Round(bounds.Y);
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        if (bitmap.Width != width || bitmap.Height != height)
            throw new ArgumentException("Overlay bitmap dimensions must match the target bounds.", nameof(bitmap));

        Debug.WriteLine($"[Overlay] 开始创建窗口 - Handle: {(_handle == IntPtr.Zero ? "NULL" : _handle.ToInt64().ToString("X"))}, x: {x}, y: {y}, w: {width}, h: {height}");

        ShowWindow(_handle, SwShowNoActivate);
        SetLastError(0);
        if (!SetWindowPos(
                _handle,
                HwndTopMost,
                0,
                0,
                0,
                0,
                SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow))
        {
            var err = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[Overlay] SetWindowPos 失败！返回值: false, 错误码: {err}");
            throw NativeFailure("Unable to place the overlay above the game window.");
        }
        IsVisible = true;
        Debug.WriteLine($"[Overlay] ShowWindow + SetWindowPos 成功，窗口已置顶！");

        EnsureCachedBuffer(width, height);

        // 极速内存拷贝：将 32bppPArgb 像素直接拷贝到常驻 DIBSection，彻底规避 16MB 的频繁申请与 GDI+ 转换
        var rect = new Rectangle(0, 0, width, height);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var srcStride = (long)bmpData.Stride;
            var dstStride = (long)width * 4;
            if (srcStride == dstStride)
            {
                CopyMemory(_cachedBitsPtr, bmpData.Scan0, (IntPtr)(dstStride * height));
            }
            else
            {
                var copyBytes = (IntPtr)Math.Min(srcStride, dstStride);
                for (var r = 0; r < height; r++)
                {
                    CopyMemory(_cachedBitsPtr + (int)(r * dstStride), bmpData.Scan0 + (int)(r * srcStride), copyBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }

        var destination = new NativePoint(x, y);
        var source = new NativePoint(0, 0);
        var size = new NativeSize(width, height);
        var blend = new BlendFunction
        {
            BlendOp = AcSrcOver,
            SourceConstantAlpha = byte.MaxValue,
            AlphaFormat = AcSrcAlpha
        };
        SetLastError(0);
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            bool ulwSuccess = UpdateLayeredWindow(
                    _handle,
                    screenDc,
                    ref destination,
                    ref size,
                    _cachedMemoryDc,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha);
            if (!ulwSuccess)
            {
                var err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[Overlay] UpdateLayeredWindow 失败！错误码: {err}");
                throw NativeFailure("Unable to update the layered overlay window.");
            }
            Debug.WriteLine($"[Overlay] UpdateLayeredWindow 成功！");
        }
        finally
        {
            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        internal int X = x;
        internal int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        internal int Width = width;
        internal int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr window,
        IntPtr destinationDc,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDc,
        ref NativePoint source,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint biSize;
        internal int biWidth;
        internal int biHeight;
        internal ushort biPlanes;
        internal ushort biBitCount;
        internal uint biCompression;
        internal uint biSizeImage;
        internal int biXPelsPerMeter;
        internal int biYPelsPerMeter;
        internal uint biClrUsed;
        internal uint biClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfoHeader pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, IntPtr length);
}
