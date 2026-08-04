using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

public readonly record struct FloorIndicatorFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    MapScreenRect ClientBounds,
    IntPtr WindowHandle);

/// <summary>Captures a tiny normalized screen ROI into a reusable 32-bit DIB.</summary>
public sealed class FloorIndicatorCaptureService : IDisposable
{
    private const string ProcessName = "dwrg";
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private readonly object _gate = new();
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private bool _disposed;

    public bool TryCapture(
        NormalizedRectangle region,
        out FloorIndicatorFrame frame,
        out double captureMilliseconds,
        out string failureReason)
    {
        var timer = Stopwatch.StartNew();
        frame = default;
        failureReason = string.Empty;
        if (_disposed)
        {
            captureMilliseconds = timer.Elapsed.TotalMilliseconds;
            failureReason = "楼层捕获器已关闭。";
            return false;
        }
        if (!region.IsValid)
        {
            captureMilliseconds = timer.Elapsed.TotalMilliseconds;
            failureReason = "楼层显示区尚未校准。";
            return false;
        }
        if (!TryGetForegroundGameClient(
                out var window,
                out var clientBounds,
                out failureReason))
        {
            captureMilliseconds = timer.Elapsed.TotalMilliseconds;
            return false;
        }

        var bounds = DwrGameWindowCaptureService.GetViewportBounds(
            clientBounds,
            region);
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);
        if (width <= 0 || height <= 0)
        {
            captureMilliseconds = timer.Elapsed.TotalMilliseconds;
            failureReason = "楼层显示区尺寸无效。";
            return false;
        }

        lock (_gate)
        {
            EnsureBuffer(width, height);
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                captureMilliseconds = timer.Elapsed.TotalMilliseconds;
                failureReason = "无法读取屏幕画面。";
                return false;
            }
            try
            {
                if (!BitBlt(
                        _memoryDc,
                        0,
                        0,
                        width,
                        height,
                        screenDc,
                        (int)Math.Round(bounds.X),
                        (int)Math.Round(bounds.Y),
                        SrcCopy | CaptureBlt))
                {
                    captureMilliseconds = timer.Elapsed.TotalMilliseconds;
                    failureReason = "无法捕获楼层显示区。";
                    return false;
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }

            if (GetForegroundWindow() != window)
            {
                captureMilliseconds = timer.Elapsed.TotalMilliseconds;
                failureReason = "捕获楼层期间前台窗口发生变化。";
                return false;
            }
            Marshal.Copy(_bits, _pixels, 0, width * height * 4);
            frame = new FloorIndicatorFrame(
                _pixels,
                width,
                height,
                width * 4,
                clientBounds,
                window);
        }

        timer.Stop();
        captureMilliseconds = timer.Elapsed.TotalMilliseconds;
        return true;
    }

    private void EnsureBuffer(int width, int height)
    {
        if (_memoryDc != IntPtr.Zero && width == _width && height == _height)
            return;
        ReleaseBuffer();
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("无法创建设备上下文。");
        try
        {
            _memoryDc = CreateCompatibleDC(screenDc);
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };
            _bitmap = CreateDIBSection(
                screenDc,
                ref info,
                0,
                out _bits,
                IntPtr.Zero,
                0);
            if (_memoryDc == IntPtr.Zero
                || _bitmap == IntPtr.Zero
                || _bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建楼层截图缓冲区。");
            }
            _previousBitmap = SelectObject(_memoryDc, _bitmap);
            _pixels = new byte[width * height * 4];
            _width = width;
            _height = height;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool TryGetForegroundGameClient(
        out IntPtr window,
        out MapScreenRect clientBounds,
        out string failureReason)
    {
        window = GetForegroundWindow();
        clientBounds = default;
        failureReason = string.Empty;
        if (window == IntPtr.Zero
            || !IsWindowVisible(window)
            || IsIconic(window))
        {
            failureReason = "游戏窗口不可见或已最小化。";
            return false;
        }
        GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(
                    process.ProcessName,
                    ProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "dwrg.exe 不是前台窗口。";
                return false;
            }
        }
        catch
        {
            failureReason = "无法确认前台游戏进程。";
            return false;
        }

        if (!GetClientRect(window, out var client)
            || client.Right <= client.Left
            || client.Bottom <= client.Top)
        {
            failureReason = "无法读取游戏客户区。";
            return false;
        }
        var origin = new NativePoint { X = client.Left, Y = client.Top };
        if (!ClientToScreen(window, ref origin))
        {
            failureReason = "无法换算游戏客户区坐标。";
            return false;
        }
        clientBounds = new MapScreenRect(
            origin.X,
            origin.Y,
            client.Right - client.Left,
            client.Bottom - client.Top);
        return true;
    }

    private void ReleaseBuffer()
    {
        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
            SelectObject(_memoryDc, _previousBitmap);
        if (_bitmap != IntPtr.Zero)
            DeleteObject(_bitmap);
        if (_memoryDc != IntPtr.Zero)
            DeleteDC(_memoryDc);
        _memoryDc = IntPtr.Zero;
        _bitmap = IntPtr.Zero;
        _previousBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _pixels = [];
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseBuffer();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc,
        ref BitmapInfo info,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);
}
