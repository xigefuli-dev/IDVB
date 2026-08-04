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
    NormalizedRectangle Bounds,
    string? Text = null);

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
    bool ShowGateMarkersOnMiniMap = true,
    bool ShowAuxiliaryAnchorsOnMiniMap = true,
    bool ShowTextAnnotationsOnMiniMap = true,
    bool ShowBoxAnnotationsOnMiniMap = true,
    float StatusOpacity = 1f,
    float StatusOffsetX = 0f,
    float StatusOffsetY = 0f,
    float MiniMapOpacity = 0.55f,
    float MiniMapOffsetX = 0f,
    float MiniMapOffsetY = 50f,
    bool ShowFloorOnMiniMap = false);

internal static class MapOverlayBitmapRenderer
{
    private const float DefaultDpi = 96f;
    private const float MapOpacity = 0.46f;
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
                    scene.ShowTextAnnotations, scene.ShowBoxAnnotations);
            if (scene.MiniMap is not null)
                DrawMiniMap(graphics, scene.MiniMap, ScaleFor(scene.Dpi),
                    scene.GameScreenBounds, scene.MonitorWorkingArea,
                    scene.MiniMapOpacity, scene.MiniMapOffsetX, scene.MiniMapOffsetY,
                    scene.ShowGateMarkersOnMiniMap, scene.ShowAuxiliaryAnchorsOnMiniMap,
                    scene.ShowTextAnnotationsOnMiniMap, scene.ShowBoxAnnotationsOnMiniMap,
                    scene.ShowGateMarkers, scene.ShowAuxiliaryAnchors,
                    scene.ShowTextAnnotations, scene.ShowBoxAnnotations,
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
                scene.ShowGateMarkers, scene.ShowAuxiliaryAnchors,
                scene.ShowTextAnnotations, scene.ShowBoxAnnotations,
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

    private static void DrawMap(Graphics graphics, MapOverlayRenderMap map,
        float dpiScale, bool allowExtendBeyondBounds = false,
        bool showGateMarkers = true, bool showAuxiliaryAnchors = true,
        bool showTextAnnotations = true, bool showBoxAnnotations = true)
    {
        if (map.Width <= 0 || map.Height <= 0 || !File.Exists(map.ImagePath))
            return;

        var mapBounds = new RectangleF(map.Left, map.Top, map.Width, map.Height);
        var graphicsState = graphics.Save();
        if (!allowExtendBeyondBounds)
        {
            var clipBounds = map.ClipBounds is { IsValid: true } clip
                ? new RectangleF(
                    (float)clip.X,
                    (float)clip.Y,
                    (float)clip.Width,
                    (float)clip.Height)
                : mapBounds;
            graphics.SetClip(clipBounds, CombineMode.Intersect);
        }
        try
        {
            lock (ImageCacheLock)
            {
                var source = GetOrLoadMapImage(map.ImagePath);
                using var attributes = new ImageAttributes();
                var colorMatrix = new ColorMatrix { Matrix33 = MapOpacity };
                attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(
                    source,
                    Rectangle.Round(mapBounds),
                    0,
                    0,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel,
                    attributes);

                var strokeWidth = Math.Max(1f, 3f * dpiScale);
                foreach (var anchor in map.Anchors)
                {
                    var isGate = anchor.Key is "main-entrance" or "side-entrance";
                    if (isGate && !showGateMarkers)
                        continue;
                    if (!isGate && !showAuxiliaryAnchors)
                        continue;

                    var bounds = anchor.Bounds;
                    var rectangle = new RectangleF(
                        map.Left + ((float)bounds.X * map.Width),
                        map.Top + ((float)bounds.Y * map.Height),
                        (float)bounds.Width * map.Width,
                        (float)bounds.Height * map.Height);
                    if (rectangle.Width <= 0 || rectangle.Height <= 0)
                        continue;

                    var color = AnchorColor(anchor.Key);
                    using var pen = new Pen(color, strokeWidth);
                    graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                    DrawAnchorLabel(graphics, anchor.DisplayName, color, rectangle, map.Top, dpiScale);
                }

                DrawAnnotations(graphics, map, dpiScale, showTextAnnotations, showBoxAnnotations);
            }
        }
        finally
        {
            graphics.Restore(graphicsState);
        }
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

    private static void DrawPlayer(
        Graphics graphics,
        MapOverlayRenderPlayer player,
        MapScreenRect? clip)
    {
        if (!File.Exists(player.ImagePath)
            || player.Width <= 0f
            || player.Height <= 0f)
        {
            return;
        }
        if (clip is { IsValid: true }
            && (player.X < clip.Value.X
                || player.Y < clip.Value.Y
                || player.X > clip.Value.X + clip.Value.Width
                || player.Y > clip.Value.Y + clip.Value.Height))
        {
            return;
        }

        var state = graphics.Save();
        if (clip is { IsValid: true } clipBounds)
        {
            graphics.SetClip(
                new RectangleF(
                    (float)clipBounds.X,
                    (float)clipBounds.Y,
                    (float)clipBounds.Width,
                    (float)clipBounds.Height),
                CombineMode.Intersect);
        }
        try
        {
            // 玩家标记贴图是静态资产，从共享缓存取，避免每帧重复加载。
            lock (ImageCacheLock)
            {
                var marker = GetOrLoadMapImage(player.ImagePath);
                graphics.DrawImage(
                    marker,
                    new RectangleF(
                        player.X - (player.Width / 2f),
                        player.Y - (player.Height / 2f),
                        player.Width,
                        player.Height));
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawMiniMap(
        Graphics graphics,
        MapOverlayRenderMap miniMap,
        float dpiScale,
        MapScreenRect gameScreenBounds,
        MapScreenRect monitorWorkingArea,
        float miniMapOpacity = 0.55f,
        float miniMapOffsetX = 0f,
        float miniMapOffsetY = 50f,
        bool showGateMarkersOnMiniMap = false,
        bool showAuxiliaryAnchorsOnMiniMap = false,
        bool showTextAnnotationsOnMiniMap = false,
        bool showBoxAnnotationsOnMiniMap = false,
        bool showGateMarkers = true,
        bool showAuxiliaryAnchors = true,
        bool showTextAnnotations = true,
        bool showBoxAnnotations = true,
        bool showFloorOnMiniMap = false)
    {
        if (miniMap.Width <= 0 || miniMap.Height <= 0
            || !File.Exists(miniMap.ImagePath))
            return;

        var margin = MiniMapMargin * dpiScale;
        var miniLeft = margin + (miniMapOffsetX * dpiScale);
        var miniTop = margin + (miniMapOffsetY * dpiScale);

        var screenMiniRight = gameScreenBounds.X + miniLeft + miniMap.Width;
        var screenMiniBottom = gameScreenBounds.Y + miniTop + miniMap.Height;

        var workRight = monitorWorkingArea.X + monitorWorkingArea.Width;
        var workBottom = monitorWorkingArea.Y + monitorWorkingArea.Height;

        if (screenMiniRight > workRight)
            miniLeft = Math.Max(0f, miniLeft - (float)(screenMiniRight - workRight));
        if (screenMiniBottom > workBottom)
            miniTop = Math.Max(0f, miniTop - (float)(screenMiniBottom - workBottom));

        miniLeft = Math.Max(0f, miniLeft);
        miniTop = Math.Max(0f, miniTop);

        var destRect = new RectangleF(miniLeft, miniTop, miniMap.Width, miniMap.Height);
        var graphicsState = graphics.Save();
        try
        {
            lock (ImageCacheLock)
            {
                var source = GetOrLoadMapImage(miniMap.ImagePath);
                using var attributes = new ImageAttributes();
                var colorMatrix = new ColorMatrix { Matrix33 = miniMapOpacity };
                attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(
                    source,
                    Rectangle.Round(destRect),
                    0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel,
                    attributes);

                var anyMiniMapAnnotation = showGateMarkersOnMiniMap
                    || showAuxiliaryAnchorsOnMiniMap
                    || showTextAnnotationsOnMiniMap
                    || showBoxAnnotationsOnMiniMap;
                if (anyMiniMapAnnotation)
                {
                    var miniState = graphics.Save();
                    graphics.TranslateTransform(miniLeft, miniTop);
                    try
                    {
                        DrawMiniMapAnchors(graphics, miniMap, dpiScale,
                            showGateMarkersOnMiniMap ? showGateMarkers : false,
                            showAuxiliaryAnchorsOnMiniMap ? showAuxiliaryAnchors : false);
                        DrawAnnotations(graphics, miniMap, dpiScale,
                            showTextAnnotationsOnMiniMap ? showTextAnnotations : false,
                            showBoxAnnotationsOnMiniMap ? showBoxAnnotations : false);
                    }
                    finally
                    {
                        graphics.Restore(miniState);
                    }
                }
                if (showFloorOnMiniMap)
                    DrawMiniMapFloorLabel(graphics, miniMap, miniLeft, miniTop, dpiScale);
            }
        }
        finally
        {
            graphics.Restore(graphicsState);
        }
    }

    private static void DrawMiniMapFloorLabel(
        Graphics graphics,
        MapOverlayRenderMap miniMap,
        float miniLeft,
        float miniTop,
        float dpiScale)
    {
        if (string.IsNullOrWhiteSpace(miniMap.FloorLabel))
            return;

        var state = graphics.Save();
        try
        {
            graphics.SetClip(new RectangleF(miniLeft, miniTop, miniMap.Width, miniMap.Height));
            using var font = CreateFont(16f * dpiScale, FontStyle.Bold);
            using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
            using var foreground = new SolidBrush(Color.White);
            var origin = new PointF(
                miniLeft + (5f * dpiScale),
                miniTop + (3f * dpiScale));
            graphics.DrawString(
                miniMap.FloorLabel,
                font,
                shadow,
                origin.X + dpiScale,
                origin.Y + dpiScale,
                StringFormat.GenericTypographic);
            graphics.DrawString(
                miniMap.FloorLabel,
                font,
                foreground,
                origin,
                StringFormat.GenericTypographic);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawMiniMapAnchors(
        Graphics graphics,
        MapOverlayRenderMap miniMap,
        float dpiScale,
        bool showGateMarkers,
        bool showAuxiliaryAnchors)
    {
        var strokeWidth = Math.Max(0.5f, 1.5f * dpiScale);
        foreach (var anchor in miniMap.Anchors)
        {
            var isGate = anchor.Key is "main-entrance" or "side-entrance";
            if (isGate && !showGateMarkers)
                continue;
            if (!isGate && !showAuxiliaryAnchors)
                continue;

            var bounds = anchor.Bounds;
            var rectangle = new RectangleF(
                miniMap.Left + ((float)bounds.X * miniMap.Width),
                miniMap.Top + ((float)bounds.Y * miniMap.Height),
                (float)bounds.Width * miniMap.Width,
                (float)bounds.Height * miniMap.Height);
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                continue;

            var color = AnchorColor(anchor.Key);
            using var pen = new Pen(color, strokeWidth);
            graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            // Skip text labels on the mini map — too small to be readable.
        }
    }

    private static void DrawAnchorLabel(
        Graphics graphics,
        string text,
        Color color,
        RectangleF anchorBounds,
        float mapTop,
        float dpiScale)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        using var font = CreateFont(12f * dpiScale, FontStyle.Regular);
        var paddingX = 4f * dpiScale;
        var paddingY = 2f * dpiScale;
        var measured = graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        var labelHeight = measured.Height + (paddingY * 2);
        var labelBounds = new RectangleF(
            anchorBounds.Left,
            Math.Max(mapTop, anchorBounds.Top - (24f * dpiScale)),
            measured.Width + (paddingX * 2),
            labelHeight);
        using var background = new SolidBrush(Color.FromArgb(178, 15, 15, 15));
        using var foreground = new SolidBrush(color);
        graphics.FillRectangle(background, labelBounds);
        graphics.DrawString(
            text,
            font,
            foreground,
            labelBounds.Left + paddingX,
            labelBounds.Top + paddingY,
            StringFormat.GenericTypographic);
    }

    private static void DrawAnnotations(
        Graphics graphics,
        MapOverlayRenderMap map,
        float dpiScale,
        bool showTextAnnotations,
        bool showBoxAnnotations)
    {
        if (map.Annotations is not { Count: > 0 })
            return;

        var strokeWidth = Math.Max(1f, 2f * dpiScale);
        foreach (var annotation in map.Annotations)
        {
            var color = AnnotationColor(annotation.ColorIndex);
            var rect = new RectangleF(
                map.Left + ((float)annotation.Bounds.X * map.Width),
                map.Top + ((float)annotation.Bounds.Y * map.Height),
                (float)annotation.Bounds.Width * map.Width,
                (float)annotation.Bounds.Height * map.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            if (annotation.Type == MapAnnotationType.Text)
            {
                if (!showTextAnnotations)
                    continue;
                using var pen = new Pen(color, strokeWidth)
                {
                    DashStyle = System.Drawing.Drawing2D.DashStyle.Dash
                };
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                if (!string.IsNullOrWhiteSpace(annotation.Text))
                    DrawAnnotationText(graphics, annotation.Text, color, rect, dpiScale);
            }
            else if (annotation.Type == MapAnnotationType.Outline)
            {
                if (!showBoxAnnotations)
                    continue;
                using var pen = new Pen(color, strokeWidth);
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }
    }

    private static void DrawAnnotationText(
        Graphics graphics,
        string text,
        Color color,
        RectangleF bounds,
        float dpiScale)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap
        };

        // Start with the largest candidate: 85 % of box height, capped at 48 px.
        var maxByHeight = bounds.Height * 0.85f;
        // Budget ~0.82 em per CJK character.
        var maxByWidth = bounds.Width / (text.Length * 0.82f);
        var fontSize = Math.Clamp(Math.Min(maxByHeight, maxByWidth), 8f * dpiScale, 48f * dpiScale);

        // Shrink iteratively until the text fits (binary-search style, but capped at
        // 4 iterations — almost always converges in 1-2).
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var font = CreateFont(fontSize, FontStyle.Bold);
            var measured = graphics.MeasureString(text, font, new SizeF(bounds.Width, bounds.Height), format);
            if (measured.Width <= bounds.Width && measured.Height <= bounds.Height)
            {
                using var brush = new SolidBrush(color);
                graphics.DrawString(text, font, brush, bounds, format);
                return;
            }

            var ratioW = bounds.Width / Math.Max(1f, measured.Width);
            var ratioH = bounds.Height / Math.Max(1f, measured.Height);
            fontSize *= Math.Min(ratioW, ratioH) * 0.95f;
            if (fontSize < 8f * dpiScale)
            {
                fontSize = 8f * dpiScale;
                using var fontMin = CreateFont(fontSize, FontStyle.Bold);
                using var brush = new SolidBrush(color);
                graphics.DrawString(text, fontMin, brush, bounds, format);
                return;
            }
        }

        // Final fallback
        using (var fontFallback = CreateFont(fontSize, FontStyle.Bold))
        using (var brushFallback = new SolidBrush(color))
        {
            graphics.DrawString(text, fontFallback, brushFallback, bounds, format);
        }
    }

    private static void DrawStatus(Graphics graphics, MapOverlayStatus status, float dpiScale,
        float opacity = 1f, float offsetX = 0f, float offsetY = 0f)
    {
        using var titleFont = CreateFont(13f * dpiScale, FontStyle.Bold);
        using var messageFont = CreateFont(12f * dpiScale, FontStyle.Regular);
        using var detailFont = CreateFont(11f * dpiScale, FontStyle.Regular);

        var maxContentWidth = 360f * dpiScale;
        var paddingX = 10f * dpiScale;
        var paddingY = 7f * dpiScale;
        var spacing = 2f * dpiScale;
        var origin = 12f * dpiScale;
        var ox = offsetX * dpiScale;
        var oy = offsetY * dpiScale;
        var opacityByte = (int)Math.Clamp(MathF.Round(opacity * 255f), 0, 255);
        var titleWidth = MeasureUnwrappedWidth(graphics, status.Title, titleFont);
        var messageWidth = MeasureUnwrappedWidth(graphics, status.Message, messageFont);
        var detailWidth = string.IsNullOrWhiteSpace(status.Detail)
            ? 0f
            : MeasureUnwrappedWidth(graphics, status.Detail, detailFont);
        var contentWidth = Math.Clamp(
            Math.Max(titleWidth, Math.Max(messageWidth, detailWidth)),
            1f,
            maxContentWidth);

        var titleSize = MeasureWrapped(graphics, status.Title, titleFont, contentWidth);
        var messageSize = MeasureWrapped(graphics, status.Message, messageFont, contentWidth);
        var detailSize = string.IsNullOrWhiteSpace(status.Detail)
            ? SizeF.Empty
            : MeasureWrapped(graphics, status.Detail, detailFont, contentWidth);
        var contentHeight = titleSize.Height + spacing + messageSize.Height;
        if (!detailSize.IsEmpty)
            contentHeight += spacing + detailSize.Height;

        var panel = new RectangleF(
            origin + ox,
            origin + oy,
            contentWidth + (paddingX * 2),
            contentHeight + (paddingY * 2));
        using var path = CreateRoundedRectangle(panel, 6f * dpiScale);
        var bgAlpha = ScaleAlpha(190, opacityByte);
        using var background = new SolidBrush(Color.FromArgb(bgAlpha, 15, 15, 15));
        graphics.FillPath(background, path);

        var textX = panel.Left + paddingX;
        var textY = panel.Top + paddingY;
        var levelColor = StatusColor(status.Level);
        using var titleBrush = new SolidBrush(Color.FromArgb(
            ScaleAlpha(levelColor.A, opacityByte), levelColor.R, levelColor.G, levelColor.B));
        using var messageBrush = new SolidBrush(Color.FromArgb(opacityByte, 255, 255, 255));
        using var detailBrush = new SolidBrush(Color.FromArgb(ScaleAlpha(210, opacityByte), 210, 210, 210));
        DrawWrapped(graphics, status.Title, titleFont, titleBrush, textX, textY, contentWidth, titleSize.Height);
        textY += titleSize.Height + spacing;
        DrawWrapped(graphics, status.Message, messageFont, messageBrush, textX, textY, contentWidth, messageSize.Height);
        if (!detailSize.IsEmpty)
        {
            textY += messageSize.Height + spacing;
            DrawWrapped(graphics, status.Detail, detailFont, detailBrush, textX, textY, contentWidth, detailSize.Height);
        }
    }

    private static void DrawWrapped(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        float x,
        float y,
        float width,
        float height)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.LineLimit
        };
        graphics.DrawString(text, font, brush, new RectangleF(x, y, width, height), format);
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

internal sealed class MapOverlayNativeWindow : IDisposable
{
    private const string WindowClassName = "IDVBuff.MapOverlay.NativeWindow";
    private const uint WsPopup = 0x80000000;
    private const uint WmNchittest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const uint UlwAlpha = 0x00000002;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly object ClassRegistrationGate = new();
    private static readonly WindowProcedureDelegate WindowProcedure = WindowProcedureCore;
    private static bool _classRegistered;

    private IntPtr _handle;
    private bool _disposed;

    internal IntPtr Handle => _handle;
    internal bool IsVisible { get; private set; }

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
                throw NativeFailure("Unable to update the layered overlay window.");
            }
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
            throw NativeFailure("Unable to place the overlay above the game window.");
        }
        IsVisible = true;
    }

    internal void Hide()
    {
        if (_handle != IntPtr.Zero)
            ShowWindow(_handle, SwHide);
        IsVisible = false;
    }

    internal MapScreenRect GetMonitorWorkingArea(IntPtr windowHandle)
    {
        var hMonitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
            return new MapScreenRect(0, 0, 3840, 2160);
        var monitorInfo = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfoW(hMonitor, ref monitorInfo))
            return new MapScreenRect(0, 0, 3840, 2160);
        return new MapScreenRect(
            monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Top,
            monitorInfo.rcWork.Right - monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top);
    }

    private void EnsureWindow()
    {
        if (_handle != IntPtr.Zero)
            return;
        EnsureWindowClass();

        SetLastError(0);
        _handle = CreateWindowEx(
            (uint)MapOverlayWindowStyles.Create(),
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
            throw NativeFailure("Unable to create the native overlay window.");

        var appliedStyles = GetWindowLongPtr(_handle, MapOverlayWindowStyles.GwlExStyle).ToInt64();
        if (!MapOverlayWindowStyles.AreApplied(appliedStyles))
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException("The native overlay window did not retain its required input styles.");
        }
        Hide();
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered)
            return;
        lock (ClassRegistrationGate)
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
            SetLastError(0);
            var atom = RegisterClassEx(ref windowClass);
            var error = Marshal.GetLastWin32Error();
            if (atom == 0 && error != ErrorClassAlreadyExists)
                throw new InvalidOperationException($"Unable to register the overlay window class (Win32 {error}).");
            _classRegistered = true;
        }
    }

    private static IntPtr WindowProcedureCore(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmNchittest)
            return new IntPtr(HtTransparent);
        if (message == WmMouseActivate)
            return new IntPtr(MaNoActivate);
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static InvalidOperationException NativeFailure(string message) =>
        new($"{message} (Win32 {Marshal.GetLastWin32Error()}).");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Hide();
        if (_handle != IntPtr.Zero)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string ClassName;
        internal IntPtr SmallIcon;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int cbSize;
        internal RECT rcMonitor;
        internal RECT rcWork;
        internal uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);
}
