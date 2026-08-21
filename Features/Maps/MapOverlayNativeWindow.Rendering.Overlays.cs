// IDVB Remaster — Overlay 叠加元素绘制方法（小地图、玩家、状态）

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

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
                    || showBoxAnnotationsOnMiniMap
                    || showLineAnnotationsOnMiniMap;
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
                            showBoxAnnotationsOnMiniMap ? showBoxAnnotations : false,
                            showLineAnnotationsOnMiniMap ? showLineAnnotations : false);
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
}
/*
 * 文件职责：MapOverlayNativeWindow.Rendering.Overlays。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
