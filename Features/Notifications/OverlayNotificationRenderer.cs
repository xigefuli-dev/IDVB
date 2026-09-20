using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 负责单张浮层通知卡片与多卡片堆叠队列的 GDI+ 高保真渲染。
/// </summary>
public static class OverlayNotificationRenderer
{
    private static readonly StringFormat TextFormat = new(StringFormatFlags.LineLimit)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.EllipsisWord
    };

    /// <summary>
    /// 在指定 Graphics 上渲染全部活动卡片。
    /// </summary>
    public static void RenderQueue(
        Graphics g,
        IReadOnlyList<OverlayNotificationCardState> cards,
        long now,
        NotificationMetrics? metrics = null)
    {
        var m = metrics ?? NotificationMetrics.Default;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var cardWidth = m.CardWidth;
        var cardX = 20f * m.Scale; // 画布水平居中安全边距

        using var font = new Font("Microsoft YaHei UI", m.FontSize, FontStyle.Regular, GraphicsUnit.Pixel);

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var currentY = card.GetInterpolatedY(now);
            var currentAlpha = card.GetInterpolatedAlpha(now);

            if (currentAlpha <= 0.005f) continue;

            RenderSingleCard(g, card, cardX, currentY, cardWidth, card.MeasuredHeight, currentAlpha, m, font);
        }
    }

    /// <summary>
    /// 渲染单张卡片：轮廓、深色填充、同字色图标、换行文本与紧贴底部的蓝色进度条。
    /// </summary>
    public static void RenderSingleCard(
        Graphics g,
        OverlayNotificationCardState card,
        float x,
        float y,
        float width,
        float height,
        float alpha,
        NotificationMetrics? metrics = null,
        Font? font = null)
    {
        var m = metrics ?? NotificationMetrics.Default;
        var scheme = OverlayNotificationTheme.GetScheme(card.Notification.Type);
        var bounds = new RectangleF(x, y, width, height);

        // 如果透明度小于 1.0，使用带 Alpha 的颜色分量
        var outlineCol = ModulateAlpha(scheme.OutlineColor, alpha);
        var fillCol = ModulateAlpha(scheme.FillColor, alpha);
        var textCol = ModulateAlpha(scheme.TextColor, alpha);

        // 1. 绘制背景圆角与更深色内容填充
        using var roundPath = OverlayNotificationTheme.CreateRoundedRectanglePath(bounds, m.CornerRadius);
        using var fillBrush = new SolidBrush(fillCol);
        g.FillPath(fillBrush, roundPath);

        // 2. 绘制更浅色边缘轮廓（按 DPI 缩放）
        using var outlinePen = new Pen(outlineCol, m.OutlineThickness);
        g.DrawPath(outlinePen, roundPath);

        // 3. 绘制左侧图标（保持内部安全边距与比例）
        var iconRect = new RectangleF(
            x + m.PaddingLeft,
            y + (height - m.IconSize) / 2f,
            m.IconSize,
            m.IconSize);

        OverlayNotificationTheme.DrawIcon(g, card.Notification.Type, iconRect, textCol, m.IconStrokeThickness);

        // 4. 绘制文本内容（支持换行，自适应高度由要点 A 保证）
        var textX = x + m.PaddingLeft + m.IconSize + m.IconGap;
        var textY = y + m.PaddingTop;
        var textRect = new RectangleF(
            textX,
            textY,
            m.AvailableTextWidth,
            height - m.PaddingTop - m.PaddingBottom);

        using var textBrush = new SolidBrush(textCol);
        bool ownFont = font is null;
        var actualFont = font ?? new Font("Microsoft YaHei UI", m.FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        try
        {
            g.DrawString(card.Notification.Message, actualFont, textBrush, textRect, TextFormat);
        }
        finally
        {
            if (ownFont) actualFont.Dispose();
        }

        // 5. 紧贴底部边缘轮廓出现一定粗细度的进度条（蓝色）
        if (card.Notification.Progress.HasValue)
        {
            var progress = (float)Math.Clamp(card.Notification.Progress.Value, 0.0, 1.0);
            if (progress > 0.001f)
            {
                var barHeight = m.ProgressBarThickness;
                var barWidth = width * progress;
                var progressRect = new RectangleF(x, y + height - barHeight, barWidth, barHeight);

                var blueCol = ModulateAlpha(OverlayNotificationTheme.ProgressBarColor, alpha);
                using var blueBrush = new SolidBrush(blueCol);

                // 将进度条剪裁在卡片下边缘圆角内，紧贴底部轮廓
                var gState = g.Save();
                using var clipRegion = new Region(roundPath);
                g.SetClip(clipRegion, CombineMode.Replace);
                g.FillRectangle(blueBrush, progressRect);
                g.Restore(gState);
            }
        }
    }

    private static Color ModulateAlpha(Color c, float alphaFactor)
    {
        var a = (int)Math.Clamp(c.A * alphaFactor, 0f, 255f);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }
}
