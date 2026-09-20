// IDVB Remaster — Overlay 地图主体绘制方法

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
    private static void DrawMap(Graphics graphics, MapOverlayRenderMap map,
        float dpiScale, bool allowExtendBeyondBounds = false,
        bool showGateMarkers = true, bool showAuxiliaryAnchors = true,
        bool showTextAnnotations = true, bool showBoxAnnotations = true,
        bool showLineAnnotations = true, float mapOpacity = 0.46f)
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
            var width = Math.Max(1, (int)Math.Round(map.Width));
            var height = Math.Max(1, (int)Math.Round(map.Height));
            var layerBitmap = GetOrBuildMapLayer(
                map, width, height, dpiScale, mapOpacity,
                showGateMarkers, showAuxiliaryAnchors,
                showTextAnnotations, showBoxAnnotations, showLineAnnotations);

            var oldInterpolation = graphics.InterpolationMode;
            var oldQuality = graphics.CompositingQuality;
            var oldSmoothing = graphics.SmoothingMode;
            var oldPixelOffset = graphics.PixelOffsetMode;
            try
            {
                // layerBitmap 在烘焙阶段已经完成了高质量 Bicubic 缩放与抗锯齿
                // 此处为 1:1 像素精确平移贴图，使用 NearestNeighbor/HighSpeed 规避 GDI+ 逐像素浮点双线性重采样（实测耗时由 45ms 降至 2ms）
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;

                graphics.DrawImage(
                    layerBitmap,
                    Rectangle.Round(mapBounds),
                    0,
                    0,
                    layerBitmap.Width,
                    layerBitmap.Height,
                    GraphicsUnit.Pixel);
            }
            finally
            {
                graphics.InterpolationMode = oldInterpolation;
                graphics.CompositingQuality = oldQuality;
                graphics.SmoothingMode = oldSmoothing;
                graphics.PixelOffsetMode = oldPixelOffset;
            }
        }
        finally
        {
            graphics.Restore(graphicsState);
        }
    }

    private static Bitmap GetOrBuildMapLayer(
        MapOverlayRenderMap map,
        int width,
        int height,
        float dpiScale,
        float mapOpacity,
        bool showGateMarkers,
        bool showAuxiliaryAnchors,
        bool showTextAnnotations,
        bool showBoxAnnotations,
        bool showLineAnnotations)
    {
        var key = $"{Path.GetFullPath(map.ImagePath)}|dpi={dpiScale:F2}|op={mapOpacity:F2}|gm={showGateMarkers}|aa={showAuxiliaryAnchors}|ta={showTextAnnotations}|ba={showBoxAnnotations}|la={showLineAnnotations}|anc={map.Anchors.Count}|ann={map.Annotations?.Count ?? 0}";
        lock (ImageCacheLock)
        {
            if (MapLayerCache.TryGetValue(key, out var cached))
            {
                if (cached.Width == width && cached.Height == height)
                    return cached.Bitmap;
                cached.Bitmap.Dispose();
                MapLayerCache.Remove(key);
            }

            var layerBitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            layerBitmap.SetResolution(dpiScale * DefaultDpi, dpiScale * DefaultDpi);

            using (var g = Graphics.FromImage(layerBitmap))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                // 底图 source 已由 GetOrLoadScaledMapImage 在局部空间精确缩放到 (width, height)
                // 此处 1:1 转移仅需应用透明度颜色矩阵，使用 NearestNeighbor/HighSpeed 避免无谓的双线性重采样
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;

                var source = GetOrLoadScaledMapImage(
                    map.ImagePath,
                    width,
                    height,
                    (uint)Math.Round(dpiScale * DefaultDpi));
                using var attributes = new ImageAttributes();
                var colorMatrix = new ColorMatrix { Matrix33 = mapOpacity };
                attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(
                    source,
                    new Rectangle(0, 0, width, height),
                    0,
                    0,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel,
                    attributes);

                // 标记与矢量注释切换为高品质抗锯齿渲染
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

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
                        (float)bounds.X * map.Width,
                        (float)bounds.Y * map.Height,
                        (float)bounds.Width * map.Width,
                        (float)bounds.Height * map.Height);
                    if (rectangle.Width <= 0 || rectangle.Height <= 0)
                        continue;

                    var color = AnchorColor(anchor.Key);
                    using var pen = new Pen(color, strokeWidth);
                    g.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                    DrawAnchorLabel(g, anchor.DisplayName, color, rectangle, 0f, dpiScale);
                }

                DrawAnnotations(g, map, dpiScale, showTextAnnotations,
                    showBoxAnnotations, showLineAnnotations);
            }

            TrimCacheIfNecessary(MapLayerCache, MaxLayerCacheEntries - 1);
            MapLayerCache[key] = new MapLayerCacheEntry(width, height, layerBitmap);
            return layerBitmap;
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
        bool showBoxAnnotations,
        bool showLineAnnotations,
        float originX = 0f,
        float originY = 0f)
    {
        if (map.Annotations is not { Count: > 0 })
            return;

        var strokeWidth = Math.Max(1f, 2f * dpiScale);
        foreach (var annotation in map.Annotations)
        {
            var color = AnnotationColor(annotation.ColorHex, annotation.ColorIndex);
            if (annotation.Type == MapAnnotationType.Line)
            {
                if (!showLineAnnotations
                    || annotation.Start?.IsValid is not true
                    || annotation.End?.IsValid is not true)
                {
                    continue;
                }
                using var linePen = new Pen(color, strokeWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(
                    linePen,
                    originX + ((float)annotation.Start.X * map.Width),
                    originY + ((float)annotation.Start.Y * map.Height),
                    originX + ((float)annotation.End.X * map.Width),
                    originY + ((float)annotation.End.Y * map.Height));
                continue;
            }
            if (annotation.Bounds?.IsValid is not true)
                continue;
            var rect = new RectangleF(
                originX + ((float)annotation.Bounds.X * map.Width),
                originY + ((float)annotation.Bounds.Y * map.Height),
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
                    DrawAnnotationText(graphics, annotation, color, rect, dpiScale, map.Width);
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
        MapOverlayRenderAnnotation annotation,
        Color color,
        RectangleF bounds,
        float dpiScale,
        float mapWidth)
    {
        var text = annotation.Text;
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var legacyStyle = annotation.FontFamily is null && annotation.FontSize is null
            && annotation.IsBold is null && annotation.IsItalic is null && annotation.IsStrikethrough is null;
        var fontStyle = FontStyle.Regular;
        if (legacyStyle || annotation.IsBold is true)
            fontStyle |= FontStyle.Bold;
        if (annotation.IsItalic is true)
            fontStyle |= FontStyle.Italic;
        if (annotation.IsStrikethrough is true)
            fontStyle |= FontStyle.Strikeout;

        // Legacy annotations preserve the historical auto-fit behavior. New text
        // starts from the selected reference size and only shrinks when necessary.
        var maxByHeight = bounds.Height * 0.85f;
        var maxByWidth = bounds.Width / (text.Length * 0.82f);
        var requestedSize = annotation.FontSize is { } configured
            ? (float)(configured * Math.Max(1f, mapWidth) / 1280f) * dpiScale
            : Math.Min(maxByHeight, maxByWidth);
        var fontSize = Math.Clamp(Math.Min(requestedSize, Math.Min(maxByHeight, maxByWidth)),
            8f * dpiScale, 48f * dpiScale);

        // Shrink iteratively until the text fits (binary-search style, but capped at
        // 4 iterations — almost always converges in 1-2).
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var font = CreateAnnotationFont(annotation.FontFamily, fontSize, fontStyle);
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
                using var fontMin = CreateAnnotationFont(annotation.FontFamily, fontSize, fontStyle);
                using var brush = new SolidBrush(color);
                graphics.DrawString(text, fontMin, brush, bounds, format);
                return;
            }
        }

        // Final fallback
        using (var fontFallback = CreateAnnotationFont(annotation.FontFamily, fontSize, fontStyle))
        using (var brushFallback = new SolidBrush(color))
        {
            graphics.DrawString(text, fontFallback, brushFallback, bounds, format);
        }
    }

    private static Font CreateAnnotationFont(string? familyName, float pixelSize, FontStyle style)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return CreateFont(pixelSize, style);
        try
        {
            return new Font(familyName, pixelSize, style, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            return CreateFont(pixelSize, style);
        }
    }
}
/*
 * 文件职责：MapOverlayNativeWindow.Rendering.Map。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
