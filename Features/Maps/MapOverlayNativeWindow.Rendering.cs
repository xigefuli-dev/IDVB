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
    float StatusScale = 1f,
    float StatusOffsetX = 0f,
    float StatusOffsetY = 0f,
    float MiniMapOpacity = 0.55f,
    float MiniMapOffsetX = 0f,
    float MiniMapOffsetY = 0f,
    bool ShowFloorOnMiniMap = false);

internal static partial class MapOverlayBitmapRenderer
{
    private sealed record ScaledImageCacheEntry(
        int Width,
        int Height,
        Bitmap Bitmap);

    private const float DefaultDpi = 96f;
    private const float MiniMapOpacity = 0.55f;
    private const float MiniMapMargin = 12f;

    private static readonly Dictionary<string, Bitmap> ImageCache = [];
    private static readonly Dictionary<string, ScaledImageCacheEntry>
        ScaledImageCache = [];
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
            foreach (var entry in ScaledImageCache.Values)
                entry.Bitmap.Dispose();
            ScaledImageCache.Clear();
        }
    }

    internal static int ScaledImageCacheCount
    {
        get
        {
            lock (ImageCacheLock)
                return ScaledImageCache.Count;
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
            DrawDynamicParts(graphics, scene);
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
        DrawDynamicParts(graphics, scene);
        return bitmap;
    }

    private static void DrawDynamicParts(Graphics graphics, MapOverlayRenderScene scene)
    {
        var dpiScale = ScaleFor(scene.Dpi);
        var statusScale = Math.Clamp(scene.StatusScale, 0f, 1f);
        var statusSize = scene.ShowStatus && scene.Status is not null && statusScale > 0f
            ? MeasureStatusPanel(graphics, scene.Status, dpiScale * statusScale)
            : (SizeF?)null;
        var miniMapSize = scene.MiniMap is { Width: > 0f, Height: > 0f } miniMap
            ? new SizeF(miniMap.Width, miniMap.Height)
            : (SizeF?)null;
        var margin = MiniMapMargin * dpiScale;
        var layout = OverlayNormalizedLayout.Resolve(
            new SizeF(
                Math.Max(0f, scene.PixelWidth - (margin * 2f)),
                Math.Max(0f, scene.PixelHeight - (margin * 2f))),
            statusSize,
            new PointF(scene.StatusOffsetX, scene.StatusOffsetY),
            miniMapSize,
            new PointF(scene.MiniMapOffsetX, scene.MiniMapOffsetY),
            8f * dpiScale);

        if (scene.MiniMap is not null && layout.MiniMap is { IsEmpty: false } miniBounds)
            DrawMiniMap(graphics, scene.MiniMap, Offset(miniBounds, margin), dpiScale,
                scene.MiniMapOpacity,
                scene.ShowGateMarkersOnMiniMap, scene.ShowAuxiliaryAnchorsOnMiniMap,
                scene.ShowTextAnnotationsOnMiniMap, scene.ShowBoxAnnotationsOnMiniMap,
                scene.ShowLineAnnotationsOnMiniMap,
                scene.ShowGateMarkers, scene.ShowAuxiliaryAnchors,
                scene.ShowTextAnnotations, scene.ShowBoxAnnotations,
                scene.ShowLineAnnotations,
                scene.ShowFloorOnMiniMap);
        if (scene.Player is not null)
            DrawPlayer(graphics, scene.Player, scene.Map?.ClipBounds);
        if (scene.Status is not null && layout.Status is { IsEmpty: false } statusBounds)
            DrawStatus(graphics, scene.Status, dpiScale * statusScale,
                Offset(statusBounds, margin).Location, scene.StatusOpacity);
    }

    private static RectangleF Offset(RectangleF rectangle, float amount) =>
        new(
            rectangle.X + amount,
            rectangle.Y + amount,
            rectangle.Width,
            rectangle.Height);

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
/*
 * 文件职责：MapOverlayNativeWindow.Rendering。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
