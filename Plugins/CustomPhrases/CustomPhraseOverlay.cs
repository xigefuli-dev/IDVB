using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.CustomPhrases;

/// <summary>
/// 非激活、鼠标穿透的短语选择层。鼠标位置由后台轮询读取，因此层本身
/// 不会抢走游戏焦点；松开触发键时返回当前高亮矩形的索引。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class CustomPhraseOverlay : IDisposable
{
    private const string WindowClassName = "IDVBuff.CustomPhraseOverlay";
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WmNchittest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const int BoxHeight = 56;
    private const int BoxGap = 12;
    private const int OuterMargin = 24;
    private const int MaximumColumns = 5;
    private static readonly object RegistrationGate = new();
    private static readonly WindowProcedureDelegate WindowProcedure = WindowProcedureCore;
    private static bool _classRegistered;

    private readonly object _sync = new();
    private IntPtr _handle;
    private CancellationTokenSource? _pollCancellation;
    private Thread? _pollThread;
    private PhraseBox[] _boxes = [];
    private Rectangle _windowBounds;
    private int _selectedIndex = -1;
    private bool _visible;
    private bool _disposed;

    public void Show(IReadOnlyList<string> phrases, PluginClientBounds gameBounds)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (phrases.Count == 0 || !gameBounds.IsValid)
            return;

        EnsureWindow();
        var boxes = CreateBoxes(phrases, gameBounds);
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            _boxes = boxes;
            _windowBounds = GetWindowBounds(boxes);
            _selectedIndex = FindSelectedIndexUnsafe();
            _visible = true;
            cancellation = new CancellationTokenSource();
            _pollCancellation?.Cancel();
            _pollCancellation = cancellation;
        }

        Render();
        SetWindowPos(
            _handle,
            HwndTopMost,
            _windowBounds.X,
            _windowBounds.Y,
            _windowBounds.Width,
            _windowBounds.Height,
            SwpNoActivate | SwpShowWindow);
        ShowWindow(_handle, SwShowNoActivate);

        var pollThread = new Thread(() =>
        {
            try
            {
                PollCursor(cancellation.Token);
            }
            finally
            {
                cancellation.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "IDVB custom phrase cursor poll"
        };
        lock (_sync)
            _pollThread = pollThread;
        pollThread.Start();
    }

    public int Hide()
    {
        CancellationTokenSource? cancellation;
        int selected;
        lock (_sync)
        {
            selected = _selectedIndex;
            _visible = false;
            cancellation = _pollCancellation;
            _pollCancellation = null;
            _pollThread = null;
        }

        cancellation?.Cancel();
        // 松开菜单键发生在 WinUI Dispatcher；这里绝不能同步 Join 轮询线程。
        // 轮询线程看到 cancellation/_visible 后自行退出并释放 CTS。
        if (_handle != IntPtr.Zero)
            ShowWindow(_handle, SwHide);
        return selected;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Hide();
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private void PollCursor(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var changed = false;
            lock (_sync)
            {
                if (!_visible)
                    return;
                var next = FindSelectedIndexUnsafe();
                if (next != _selectedIndex)
                {
                    _selectedIndex = next;
                    changed = true;
                }
            }
            if (changed)
                Render();
            cancellationToken.WaitHandle.WaitOne(10);
        }
    }

    private int FindSelectedIndexUnsafe()
    {
        if (!GetCursorPos(out var point))
            return -1;
        for (var index = 0; index < _boxes.Length; index++)
        {
            if (_boxes[index].Bounds.Contains(point.X, point.Y))
                return index;
        }
        return -1;
    }

    private void Render()
    {
        PhraseBox[] boxes;
        Rectangle windowBounds;
        int selected;
        lock (_sync)
        {
            if (!_visible || _handle == IntPtr.Zero)
                return;
            boxes = _boxes.ToArray();
            windowBounds = _windowBounds;
            selected = _selectedIndex;
        }

        using var bitmap = new Bitmap(
            Math.Max(1, windowBounds.Width),
            Math.Max(1, windowBounds.Height),
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var box in boxes)
            {
                var local = new Rectangle(
                    box.Bounds.X - windowBounds.X,
                    box.Bounds.Y - windowBounds.Y,
                    box.Bounds.Width,
                    box.Bounds.Height);
                using var background = new SolidBrush(Color.FromArgb(218, 16, 22, 32));
                using var border = new Pen(
                    box.Index == selected
                        ? Color.FromArgb(255, 255, 188, 74)
                        : Color.FromArgb(225, 190, 205, 222),
                    box.Index == selected ? 4f : 2f);
                graphics.FillRectangle(background, local);
                graphics.DrawRectangle(border, local);
                using var textBrush = new SolidBrush(Color.White);
                graphics.DrawString(
                    CustomPhrasePluginData.ToDisplayText(box.Phrase),
                    font,
                    textBrush,
                    local,
                    format);
            }
        }

        UpdateLayeredBitmap(bitmap, windowBounds);
    }

    private static PhraseBox[] CreateBoxes(
        IReadOnlyList<string> phrases,
        PluginClientBounds gameBounds)
    {
        var count = Math.Min(CustomPhrasePluginData.MaxPhraseCount, phrases.Count);
        var usableWidth = Math.Max(1, gameBounds.Width - (OuterMargin * 2));
        var columns = Math.Min(MaximumColumns, count);
        var width = Math.Clamp(
            (int)Math.Round(gameBounds.Width * 0.14d),
            150,
            260);
        width = Math.Min(width,
            Math.Max(40, (usableWidth - (BoxGap * (columns - 1))) / columns));
        var rows = (int)Math.Ceiling(count / (double)columns);
        var totalHeight = (rows * BoxHeight) + (BoxGap * (rows - 1));
        var centerY = gameBounds.Y + (int)Math.Round(gameBounds.Height * 0.70d);
        var top = Math.Clamp(
            centerY - (totalHeight / 2),
            gameBounds.Y + OuterMargin,
            gameBounds.Y + gameBounds.Height - OuterMargin - totalHeight);
        var boxes = new List<PhraseBox>(count);
        for (var row = 0; row < rows; row++)
        {
            var firstIndex = row * columns;
            var rowCount = Math.Min(columns, count - firstIndex);
            var rowWidth = (rowCount * width) + (BoxGap * (rowCount - 1));
            var left = gameBounds.X + ((gameBounds.Width - rowWidth) / 2);
            for (var column = 0; column < rowCount; column++)
            {
                var index = firstIndex + column;
                boxes.Add(new PhraseBox(
                    index,
                    phrases[index],
                    new Rectangle(
                        left + ((width + BoxGap) * column),
                        top + ((BoxHeight + BoxGap) * row),
                        width,
                        BoxHeight)));
            }
        }
        return boxes.ToArray();
    }

    private static Rectangle GetWindowBounds(IReadOnlyList<PhraseBox> boxes)
    {
        var bounds = boxes[0].Bounds;
        for (var index = 1; index < boxes.Count; index++)
            bounds = Rectangle.Union(bounds, boxes[index].Bounds);
        return bounds;
    }

    private void EnsureWindow()
    {
        if (_handle != IntPtr.Zero)
            return;
        EnsureWindowClass();
        _handle = CreateWindowEx(
            WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
            WindowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("无法创建自定义短语选择层。");
    }

    private void UpdateLayeredBitmap(Bitmap bitmap, Rectangle bounds)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        if (screenDc == IntPtr.Zero || memoryDc == IntPtr.Zero)
        {
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("无法创建自定义短语选择层的绘图表面。");
        }

        var bitmapHandle = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            bitmapHandle = bitmap.GetHbitmap(Color.Transparent);
            previousObject = SelectObject(memoryDc, bitmapHandle);
            var destination = new NativePoint(bounds.X, bounds.Y);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = byte.MaxValue,
                AlphaFormat = AcSrcAlpha
            };
            if (!UpdateLayeredWindow(
                    _handle,
                    screenDc,
                    ref destination,
                    ref size,
                    memoryDc,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha))
            {
                throw new InvalidOperationException("无法刷新自定义短语选择层。");
            }
        }
        finally
        {
            if (previousObject != IntPtr.Zero)
                SelectObject(memoryDc, previousObject);
            if (bitmapHandle != IntPtr.Zero)
                DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered)
            return;
        lock (RegistrationGate)
        {
            if (_classRegistered)
                return;
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Instance = GetModuleHandle(null),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                ClassName = WindowClassName
            };
            if (RegisterClassEx(ref windowClass) == 0
                && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException("无法注册自定义短语选择层窗口类。");
            }
            _classRegistered = true;
        }
    }

    private static IntPtr WindowProcedureCore(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (message == WmNchittest)
            return new IntPtr(HtTransparent);
        if (message == WmMouseActivate)
            return new IntPtr(MaNoActivate);
        return DefWindowProc(window, message, wParam, lParam);
    }

    private readonly record struct PhraseBox(int Index, string Phrase, Rectangle Bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    private static readonly IntPtr HwndTopMost = new(-1);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
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
}
