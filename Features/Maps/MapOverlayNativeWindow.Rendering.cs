using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

public enum MapOverlayStatusLevel
{
    Ready,
    Scanning,
    Success,
    Warning,
    Failure,
    ManualSelection
}

public sealed record MapOverlayStatus(
    MapOverlayStatusLevel Level,
    string Title,
    string Message,
    string Detail = "");

internal sealed record MapOverlayRenderAnchor(
    string Key,
    string DisplayName,
    NormalizedRectangle Bounds);

internal sealed record MapOverlayRenderAnnotation(
    MapAnnotationType Type,
    int ColorIndex,
    string ColorHex,
    NormalizedRectangle? Bounds,
    NormalizedPoint? Start,
    NormalizedPoint? End,
    string? Text = null,
    string? FontFamily = null,
    double? FontSize = null,
    bool? IsBold = null,
    bool? IsItalic = null,
    bool? IsStrikethrough = null);

internal sealed record MapOverlayRenderMap(
    string ImagePath,
    float Left,
    float Top,
    float Width,
    float Height,
    IReadOnlyList<MapOverlayRenderAnchor> Anchors,
    MapScreenRect? ClipBounds = null,
    IReadOnlyList<MapOverlayRenderAnnotation>? Annotations = null,
    string? FloorLabel = null);

internal sealed record MapOverlayRenderPlayer(
    PlayerSlot PlayerSlot,
    string ImagePath,
    float X,
    float Y,
    float Width,
    float Height,
    float Confidence);

internal sealed record MapOverlayRenderScene(
    int PixelWidth,
    int PixelHeight,
    uint Dpi,
    MapOverlayRenderMap? Map,
    MapOverlayStatus? Status,
    bool ShowStatus,
    MapOverlayRenderPlayer? Player = null,
    MapOverlayRenderMap? MiniMap = null,
    bool AllowMapExtendBeyondBounds = false,
    MapScreenRect GameScreenBounds = default,
    MapScreenRect MonitorWorkingArea = default,
    bool ShowGateMarkers = true,
    bool ShowAuxiliaryAnchors = true,
    bool ShowTextAnnotations = true,
    bool ShowBoxAnnotations = true,
    bool ShowLineAnnotations = true,
    bool ShowGateMarkersOnMiniMap = true,
    bool ShowAuxiliaryAnchorsOnMiniMap = true,
    bool ShowTextAnnotationsOnMiniMap = true,
    bool ShowBoxAnnotationsOnMiniMap = true,
    bool ShowLineAnnotationsOnMiniMap = true,
    float MapOpacity = 0.46f,
    float StatusOpacity = 1f,
    float StatusOffsetX = 0f,
    float StatusOffsetY = 0f,
    float MiniMapOpacity = 0.55f,
    float MiniMapOffsetX = 0f,
    float MiniMapOffsetY = 50f,
    bool ShowFloorOnMiniMap = false);

internal static partial class MapOverlayBitmapRenderer
{
    private const float DefaultDpi = 96f;
    private const float MiniMapOpacity = 0.55f;
    private const float MiniMapMargin = 12f;

    private static readonly Dictionary<string, Bitmap> ImageCache = [];
    private static readonly Lock ImageCacheLock = new();

    /// <summary>
    /// Drops images loaded before a map catalog refresh. Rendering is guarded
    /// so a bitmap cannot be disposed while a frame is using it.
    /// </summary>
    internal static void InvalidateImageCache()
    {
        lock (ImageCacheLock)
        {
            foreach (var image in ImageCache.Values)
                image.Dispose();
            ImageCache.Clear();
        }
    }

    internal static bool TryGetScaledImageSize(
        string imagePath,
        double scale,
        out float width,
        out float height)
    {
        width = 0f;
        height = 0f;
        if (string.IsNullOrWhiteSpace(imagePath)
            || !File.Exists(imagePath)
            || !double.IsFinite(scale)
            || scale <= 0d)
        {
            return false;
        }

        try
        {
            using var source = new Bitmap(imagePath);
            var scaledWidth = source.Width * scale;
            var scaledHeight = source.Height * scale;
            if (source.Width <= 0
                || source.Height <= 0
                || !double.IsFinite(scaledWidth)
                || !double.IsFinite(scaledHeight)
                || scaledWidth <= 0d
                || scaledHeight <= 0d
                || scaledWidth > float.MaxValue
                || scaledHeight > float.MaxValue)
            {
                return false;
            }

            width = (float)scaledWidth;
            height = (float)scaledHeight;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static Bitmap Render(MapOverlayRenderScene scene)
    {
        if (scene.PixelWidth <= 0 || scene.PixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(scene), "Overlay dimensions must be positive.");

        var bitmap = new Bitmap(
            scene.PixelWidth,
            scene.PixelHeight,
            PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(scene.Dpi == 0 ? DefaultDpi : scene.Dpi, scene.Dpi == 0 ? DefaultDpi : scene.Dpi);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (scene.Map is not null)
                DrawMap(graphics, scene.Map, ScaleFor(scene.Dpi), scene.AllowMapExtendBeyondBounds,
                    scene.ShowGateMarkers, scene.ShowAuxiliaryAnchors,
                    scene.ShowTextAnnotations, scene.ShowBoxAnnotations,
                    scene.ShowLineAnnotations, scene.MapOpacity);
            if (scene.MiniMap is not null)
                DrawMiniMap(graphics, scene.MiniMap, ScaleFor(scene.Dpi),
                    scene.GameScreenBounds, scene.MonitorWorkingArea,
                    scene.MiniMapOpacity, scene.MiniMapOffsetX, scene.MiniMapOffsetY,
                    scene.ShowGateMarkersOnMiniMap, scene.ShowAuxiliaryAnchorsOnMiniMap,
                    scene.ShowTextAnnotationsOnMiniMap, scene.ShowBoxAnnotationsOnMiniMap,
                    scene.ShowLineAnnotationsOnMiniMap,
                    scene.ShowGateMarkers, scene.ShowAuxiliaryAnchors,
                    scene.ShowTextAnnotations, scene.ShowBoxAnnotations,
                    scene.ShowLineAnnotations,
                    scene.ShowFloorOnMiniMap);
            if (scene.Player is not null)
                DrawPlayer(
                    graphics,
                    scene.Player,
                    scene.Map?.ClipBounds);
            if (scene.ShowStatus && scene.Status is not null)
                DrawStatus(graphics, scene.Status, ScaleFor(scene.Dpi),
                    scene.StatusOpacity, scene.StatusOffsetX, scene.StatusOffsetY);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static Bitmap ComposeDynamic(
        Bitmap lockedBackground,
        MapOverlayRenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(lockedBackground);
        if (lockedBackground.Width != scene.PixelWidth
            || lockedBackground.Height != scene.PixelHeight)
        {
            throw new ArgumentException(
                "The locked background size does not match the overlay scene.",
                nameof(lockedBackground));
        }

        var bitmap = new Bitmap(lockedBackground);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (scene.MiniMap is not null)
            DrawMiniMap(graphics, scene.MiniMap, ScaleFor(scene.Dpi),
                scene.GameScreenBounds, scene.MonitorWorkingArea,
                scene.MiniMapOpacity, scene.MiniMapOffsetX, scene.MiniMapOffsetY,
                scene.ShowGateMarkersOnMiniMap, scene.ShowAuxiliaryAnchorsOnMiniMap,
                scene.ShowTextAnnotationsOnMiniMap, scene.ShowBoxAnnotationsOnMiniMap,
                scene.ShowLineAnnotationsOnMiniMap,
                scene.ShowGateMarkers, scene.ShowAuxiliaryAnchors,
                scene.ShowTextAnnotations, scene.ShowBoxAnnotations,
                scene.ShowLineAnnotations,
                scene.ShowFloorOnMiniMap);
        if (scene.Player is not null)
        {
            DrawPlayer(
                graphics,
                scene.Player,
                scene.Map?.ClipBounds);
        }
        if (scene.ShowStatus && scene.Status is not null)
            DrawStatus(graphics, scene.Status, ScaleFor(scene.Dpi),
                scene.StatusOpacity, scene.StatusOffsetX, scene.StatusOffsetY);
        return bitmap;
    }

    private static Bitmap GetOrLoadMapImage(string imagePath)
    {
        lock (ImageCacheLock)
        {
            if (ImageCache.TryGetValue(imagePath, out var cached) && cached is not null)
                return cached;
            var bytes = File.ReadAllBytes(imagePath);
            using var stream = new MemoryStream(bytes, writable: false);
            var loaded = new Bitmap(stream);
            if (!ImageCache.TryAdd(imagePath, loaded))
            {
                loaded.Dispose();
                return ImageCache[imagePath];
            }
            return loaded;
        }
    }

    private static SizeF MeasureWrapped(Graphics graphics, string text, Font font, float width)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.LineLimit
        };
        return graphics.MeasureString(text, font, new SizeF(width, 10000f), format);
    }

    private static float MeasureUnwrappedWidth(Graphics graphics, string text, Font font) =>
        Math.Max(1f, graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width);

    private static Font CreateFont(float pixelSize, FontStyle style) =>
        new("Segoe UI", Math.Max(1f, pixelSize), style, GraphicsUnit.Pixel);

    private static float ScaleFor(uint dpi) => Math.Max(1f, (dpi == 0 ? DefaultDpi : dpi) / DefaultDpi);

    private static int ScaleAlpha(int sourceAlpha, int opacityByte) =>
        Math.Clamp(sourceAlpha * opacityByte / 255, 0, 255);

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Color AnchorColor(string key) => key switch
    {
        "main-entrance" => Color.FromArgb(255, 38, 133, 255),
        "side-entrance" => Color.FromArgb(255, 63, 207, 123),
        "second-floor-primary" => Color.FromArgb(255, 132, 94, 247),
        _ => Color.FromArgb(255, 236, 150, 61)
    };

    internal static Color AnnotationColor(int colorIndex) => colorIndex switch
    {
        0 => Color.FromArgb(255, 255, 59, 48),
        1 => Color.FromArgb(255, 255, 149, 0),
        2 => Color.FromArgb(255, 255, 204, 0),
        3 => Color.FromArgb(255, 52, 199, 89),
        4 => Color.FromArgb(255, 50, 173, 230),
        5 => Color.FromArgb(255, 0, 122, 255),
        6 => Color.FromArgb(255, 175, 82, 222),
        7 => Color.FromArgb(255, 255, 45, 85),
        _ => Color.FromArgb(255, 242, 242, 242)
    };

    internal static Color AnnotationColor(string? colorHex, int fallbackIndex)
    {
        if (!MapAnnotationColor.TryNormalize(colorHex, out var normalized))
            return AnnotationColor(fallbackIndex);
        return Color.FromArgb(
            255,
            Convert.ToInt32(normalized.Substring(1, 2), 16),
            Convert.ToInt32(normalized.Substring(3, 2), 16),
            Convert.ToInt32(normalized.Substring(5, 2), 16));
    }

    internal static Color StatusColor(MapOverlayStatusLevel level) => level switch
    {
        MapOverlayStatusLevel.Success => Color.FromArgb(255, 91, 220, 138),
        MapOverlayStatusLevel.Warning => Color.FromArgb(255, 255, 184, 77),
        MapOverlayStatusLevel.Failure => Color.FromArgb(255, 255, 105, 97),
        MapOverlayStatusLevel.Scanning => Color.FromArgb(255, 91, 176, 255),
        MapOverlayStatusLevel.ManualSelection => Color.FromArgb(255, 115, 193, 255),
        _ => Color.FromArgb(255, 225, 225, 225)
    };
}

internal sealed partial class MapOverlayNativeWindow
{
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

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);
            throw NativeFailure("Unable to create the overlay memory device context.");
        }

        var bitmapHandle = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            if (bitmapHandle == IntPtr.Zero)
                throw NativeFailure("Unable to create the overlay bitmap handle.");
            previousObject = SelectObject(memoryDc, bitmapHandle);
            if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
                throw NativeFailure("Unable to select the overlay bitmap.");

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
            bool ulwSuccess = UpdateLayeredWindow(
                    _handle,
                    screenDc,
                    ref destination,
                    ref size,
                    memoryDc,
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
            if (previousObject != IntPtr.Zero && previousObject != new IntPtr(-1))
                SelectObject(memoryDc, previousObject);
            if (bitmapHandle != IntPtr.Zero)
                DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
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
}
