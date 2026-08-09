// IDVB Remaster — Overlay 地图主体绘制方法

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
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
}
