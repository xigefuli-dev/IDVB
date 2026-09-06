// IDVB Remaster — Overlay 叠加元素绘制方法（小地图、玩家、状态）

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
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

    private static Bitmap GetOrBuildMiniMapLayer(
        MapOverlayRenderMap miniMap,
        int width,
        int height,
        float dpiScale,
        float miniMapOpacity,
        bool showGateMarkersOnMiniMap,
        bool showAuxiliaryAnchorsOnMiniMap,
        bool showTextAnnotationsOnMiniMap,
        bool showBoxAnnotationsOnMiniMap,
        bool showLineAnnotationsOnMiniMap,
        bool showGateMarkers,
        bool showAuxiliaryAnchors,
        bool showTextAnnotations,
        bool showBoxAnnotations,
        bool showLineAnnotations,
        bool showFloorOnMiniMap)
    {
        var gm = showGateMarkersOnMiniMap && showGateMarkers;
        var aa = showAuxiliaryAnchorsOnMiniMap && showAuxiliaryAnchors;
        var ta = showTextAnnotationsOnMiniMap && showTextAnnotations;
        var ba = showBoxAnnotationsOnMiniMap && showBoxAnnotations;
        var la = showLineAnnotationsOnMiniMap && showLineAnnotations;
        var key = $"{Path.GetFullPath(miniMap.ImagePath)}|w={width}|h={height}|dpi={dpiScale:F2}|op={miniMapOpacity:F2}|gm={gm}|aa={aa}|ta={ta}|ba={ba}|la={la}|fl={showFloorOnMiniMap}|flbl={miniMap.FloorLabel}|anc={miniMap.Anchors.Count}|ann={miniMap.Annotations?.Count ?? 0}";

        lock (ImageCacheLock)
        {
            if (MiniMapLayerCache.TryGetValue(key, out var cached))
            {
                if (cached.Width == width && cached.Height == height)
                    return cached.Bitmap;
                cached.Bitmap.Dispose();
                MiniMapLayerCache.Remove(key);
            }

            var layerBitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            layerBitmap.SetResolution(dpiScale * DefaultDpi, dpiScale * DefaultDpi);

            using (var g = Graphics.FromImage(layerBitmap))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                var source = GetOrLoadMapImage(miniMap.ImagePath);
                using var attributes = new ImageAttributes();
                var colorMatrix = new ColorMatrix { Matrix33 = miniMapOpacity };
                attributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                g.DrawImage(
                    source,
                    new Rectangle(0, 0, width, height),
                    0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel,
                    attributes);

                var effectiveMiniMap = miniMap with
                {
                    Left = 0,
                    Top = 0,
                    Width = width,
                    Height = height
                };

                if (gm || aa)
                {
                    DrawMiniMapAnchors(g, effectiveMiniMap, dpiScale, gm, aa);
                }

                if (ta || ba || la)
                {
                    DrawAnnotations(g, effectiveMiniMap, dpiScale, ta, ba, la);
                }

                if (showFloorOnMiniMap)
                {
                    DrawMiniMapFloorLabel(g, effectiveMiniMap, 0f, 0f, dpiScale);
                }
            }

            MiniMapLayerCache[key] = new MapLayerCacheEntry(width, height, layerBitmap);
            return layerBitmap;
        }
    }

    private static void DrawMiniMap(
        Graphics graphics,
        MapOverlayRenderMap miniMap,
        RectangleF destRect,
        float dpiScale,
        float miniMapOpacity = 0.55f,
        bool showGateMarkersOnMiniMap = false,
        bool showAuxiliaryAnchorsOnMiniMap = false,
        bool showTextAnnotationsOnMiniMap = false,
        bool showBoxAnnotationsOnMiniMap = false,
        bool showLineAnnotationsOnMiniMap = false,
        bool showGateMarkers = true,
        bool showAuxiliaryAnchors = true,
        bool showTextAnnotations = true,
        bool showBoxAnnotations = true,
        bool showLineAnnotations = true,
        bool showFloorOnMiniMap = false)
    {
        if (miniMap.Width <= 0 || miniMap.Height <= 0
            || !File.Exists(miniMap.ImagePath))
            return;

        var targetWidth = Math.Max(1, (int)Math.Round(destRect.Width));
        var targetHeight = Math.Max(1, (int)Math.Round(destRect.Height));

        var layerBitmap = GetOrBuildMiniMapLayer(
            miniMap,
            targetWidth,
            targetHeight,
            dpiScale,
            miniMapOpacity,
            showGateMarkersOnMiniMap,
            showAuxiliaryAnchorsOnMiniMap,
            showTextAnnotationsOnMiniMap,
            showBoxAnnotationsOnMiniMap,
            showLineAnnotationsOnMiniMap,
            showGateMarkers,
            showAuxiliaryAnchors,
            showTextAnnotations,
            showBoxAnnotations,
            showLineAnnotations,
            showFloorOnMiniMap);

        var oldInterpolation = graphics.InterpolationMode;
        var oldQuality = graphics.CompositingQuality;
        var oldSmoothing = graphics.SmoothingMode;
        var oldPixelOffset = graphics.PixelOffsetMode;
        try
        {
            // layerBitmap 在烘焙阶段已经完成了高质量 Bicubic 缩放与抗锯齿
            // 此处为 1:1 像素精确平移贴图，使用 NearestNeighbor/HighSpeed 规避 GDI+ 逐像素重采样（实测耗时降至 0.1ms 级）
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;

            graphics.DrawImage(
                layerBitmap,
                Rectangle.Round(destRect),
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

    private static SizeF MeasureStatusPanel(
        Graphics graphics,
        MapOverlayStatus status,
        float scale)
    {
        using var titleFont = CreateFont(13f * scale, FontStyle.Bold);
        using var messageFont = CreateFont(12f * scale, FontStyle.Regular);
        using var detailFont = CreateFont(11f * scale, FontStyle.Regular);

        var maxContentWidth = 360f * scale;
        var paddingX = 10f * scale;
        var paddingY = 7f * scale;
        var spacing = 2f * scale;
        var contentWidth = MeasureStatusContentWidth(
            graphics, status, titleFont, messageFont, detailFont, maxContentWidth);
        var contentHeight = MeasureStatusContentHeight(
            graphics, status, titleFont, messageFont, detailFont, contentWidth, spacing);
        return new SizeF(
            contentWidth + (paddingX * 2f),
            contentHeight + (paddingY * 2f));
    }

    private static void DrawStatus(
        Graphics graphics,
        MapOverlayStatus status,
        float scale,
        PointF location,
        float opacity = 1f)
    {
        using var titleFont = CreateFont(13f * scale, FontStyle.Bold);
        using var messageFont = CreateFont(12f * scale, FontStyle.Regular);
        using var detailFont = CreateFont(11f * scale, FontStyle.Regular);

        var maxContentWidth = 360f * scale;
        var paddingX = 10f * scale;
        var paddingY = 7f * scale;
        var spacing = 2f * scale;
        var opacityByte = (int)Math.Clamp(MathF.Round(opacity * 255f), 0, 255);
        var contentWidth = MeasureStatusContentWidth(
            graphics, status, titleFont, messageFont, detailFont, maxContentWidth);

        var titleSize = MeasureWrapped(graphics, status.Title, titleFont, contentWidth);
        var messageSize = MeasureWrapped(graphics, status.Message, messageFont, contentWidth);
        var detailSize = string.IsNullOrWhiteSpace(status.Detail)
            ? SizeF.Empty
            : MeasureWrapped(graphics, status.Detail, detailFont, contentWidth);
        var contentHeight = titleSize.Height + spacing + messageSize.Height;
        if (!detailSize.IsEmpty)
            contentHeight += spacing + detailSize.Height;

        var panel = new RectangleF(
            location.X,
            location.Y,
            contentWidth + (paddingX * 2),
            contentHeight + (paddingY * 2));
        using var path = CreateRoundedRectangle(panel, 6f * scale);
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

    private static float MeasureStatusContentWidth(
        Graphics graphics,
        MapOverlayStatus status,
        Font titleFont,
        Font messageFont,
        Font detailFont,
        float maximum)
    {
        var titleWidth = MeasureUnwrappedWidth(graphics, status.Title, titleFont);
        var messageWidth = MeasureUnwrappedWidth(graphics, status.Message, messageFont);
        var detailWidth = string.IsNullOrWhiteSpace(status.Detail)
            ? 0f
            : MeasureUnwrappedWidth(graphics, status.Detail, detailFont);
        return Math.Clamp(
            Math.Max(titleWidth, Math.Max(messageWidth, detailWidth)),
            Math.Min(1f, maximum),
            maximum);
    }

    private static float MeasureStatusContentHeight(
        Graphics graphics,
        MapOverlayStatus status,
        Font titleFont,
        Font messageFont,
        Font detailFont,
        float contentWidth,
        float spacing)
    {
        var titleSize = MeasureWrapped(graphics, status.Title, titleFont, contentWidth);
        var messageSize = MeasureWrapped(graphics, status.Message, messageFont, contentWidth);
        var detailSize = string.IsNullOrWhiteSpace(status.Detail)
            ? SizeF.Empty
            : MeasureWrapped(graphics, status.Detail, detailFont, contentWidth);
        var height = titleSize.Height + spacing + messageSize.Height;
        if (!detailSize.IsEmpty)
            height += spacing + detailSize.Height;
        return height;
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
}
/*
 * 文件职责：MapOverlayNativeWindow.Rendering.Overlays。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
