using System.Drawing;
using System.Drawing.Drawing2D;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 专属浮层通知的视觉主题与配色方案。
/// 严格遵守“更浅色的边缘轮廓 + 更深色的内容填充，字体介于两者之间”的对比度规范。
/// </summary>
public static class OverlayNotificationTheme
{
    // 卡片整体尺寸（玩家反馈：原来的 520×64 在 1080p 上要占掉四分之一屏宽，太大）。
    // 这里按 0.58 左右收敛：宽 300、高 42、字 13px，约屏宽 15%，仍能放下一行中文提示。
    public const float CardWidth = 300f;
    public const float MinCardHeight = 42f;
    public const float CornerRadius = 7f;
    public const float CardSpacing = 8f;
    public const float PaddingLeft = 11f;
    public const float PaddingRight = 11f;
    public const float PaddingTop = 10f;
    public const float PaddingBottom = 10f;
    public const float IconSize = 18f;
    public const float IconGap = 8f;
    public const float ProgressBarThickness = 3f;

    /// <summary>文字有效绘制宽度（用于换行自适应测量）。</summary>
    public static float AvailableTextWidth => CardWidth - (PaddingLeft + IconSize + IconGap + PaddingRight);

    /// <summary>进度条固定使用纯粹高对比电光蓝色（确保深浅色可分辨性及与三套配色的对比度）。</summary>
    public static readonly Color ProgressBarColor = Color.FromArgb(255, 0, 145, 234);

    /// <summary>
    /// 获取指定通知类型的配色方案。
    /// </summary>
    public static NotificationColorScheme GetScheme(OverlayNotificationType type) => type switch
    {
        OverlayNotificationType.Error => ErrorScheme,
        OverlayNotificationType.Warning => WarningScheme,
        OverlayNotificationType.Notice => NoticeScheme,
        _ => NoticeScheme
    };

    /// <summary>
    /// 错误（红色系）：更浅色浅粉红轮廓 + 更深色暗红酒填充 + 介于两者之间的鲜亮珊瑚红字色。
    /// </summary>
    public static readonly NotificationColorScheme ErrorScheme = new(
        OutlineColor: Color.FromArgb(255, 255, 170, 170),
        FillColor: Color.FromArgb(240, 36, 10, 14),
        TextColor: Color.FromArgb(255, 255, 125, 125));

    /// <summary>
    /// 警告（橙色系）：更浅色浅琥珀金轮廓 + 更深色焦褐暗橙填充 + 介于两者之间的温暖金橙字色。
    /// </summary>
    public static readonly NotificationColorScheme WarningScheme = new(
        OutlineColor: Color.FromArgb(255, 255, 224, 130),
        FillColor: Color.FromArgb(240, 45, 26, 8),
        TextColor: Color.FromArgb(255, 255, 167, 38));

    /// <summary>
    /// 通知（绿色系）：更浅色浅薄荷绿轮廓 + 更深色墨绿填充 + 介于两者之间的鲜活翠绿字色。
    /// </summary>
    public static readonly NotificationColorScheme NoticeScheme = new(
        OutlineColor: Color.FromArgb(255, 134, 239, 172),
        FillColor: Color.FromArgb(240, 10, 36, 18),
        TextColor: Color.FromArgb(255, 52, 211, 153));

    /// <summary>
    /// 构建卡片圆角矩形几何路径。
    /// </summary>
    public static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0f)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 绘制左侧对应图标（错误、警告、对话泡泡 💬），完全使用与字体相同的颜色方案。
    /// </summary>
    public static void DrawIcon(
        Graphics g,
        OverlayNotificationType type,
        RectangleF bounds,
        Color color,
        float strokeThickness = 2.0f)
    {
        using var pen = new Pen(color, strokeThickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);

        switch (type)
        {
            case OverlayNotificationType.Error:
                // 绘制错误图标（圆环内的叉号，使用与尺寸成比例的内外边距）
                var inset = bounds.Width * 0.24f;
                g.DrawLine(pen, bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
                g.DrawLine(pen, bounds.Right - inset, bounds.Top + inset, bounds.Left + inset, bounds.Bottom - inset);
                var ringInset = strokeThickness * 0.5f;
                g.DrawEllipse(pen, bounds.Left + ringInset, bounds.Top + ringInset, bounds.Width - ringInset * 2f, bounds.Height - ringInset * 2f);
                break;

            case OverlayNotificationType.Warning:
                // 绘制警告图标（平滑圆角三角形 + 感叹号）
                var triPath = new GraphicsPath();
                var cx = bounds.Left + bounds.Width / 2f;
                var topY = bounds.Top + bounds.Height * 0.08f;
                var bottomY = bounds.Bottom - bounds.Height * 0.08f;
                triPath.AddLine(cx, topY, bounds.Right - bounds.Width * 0.08f, bottomY);
                triPath.AddLine(bounds.Right - bounds.Width * 0.08f, bottomY, bounds.Left + bounds.Width * 0.08f, bottomY);
                triPath.CloseFigure();
                g.DrawPath(pen, triPath);

                // 感叹号竖线与圆点
                var barTop = topY + (bottomY - topY) * 0.38f;
                var barBottom = topY + (bottomY - topY) * 0.68f;
                g.DrawLine(pen, cx, barTop, cx, barBottom);
                var dotRadius = Math.Max(1.0f, bounds.Width * 0.07f);
                g.FillEllipse(brush, cx - dotRadius, bottomY - dotRadius * 2.5f, dotRadius * 2f, dotRadius * 2f);
                break;

            case OverlayNotificationType.Notice:
            default:
                // 绘制对话泡泡标志 💬（左侧圆角气泡 + 底部小尾巴）
                var bubble = new GraphicsPath();
                var bubbleRect = new RectangleF(bounds.Left + bounds.Width * 0.05f, bounds.Top + bounds.Height * 0.05f, bounds.Width * 0.9f, bounds.Height * 0.72f);
                var bDiam = Math.Max(3f, bubbleRect.Height * 0.42f);
                bubble.AddArc(bubbleRect.Left, bubbleRect.Top, bDiam, bDiam, 180, 90);
                bubble.AddArc(bubbleRect.Right - bDiam, bubbleRect.Top, bDiam, bDiam, 270, 90);
                bubble.AddArc(bubbleRect.Right - bDiam, bubbleRect.Bottom - bDiam, bDiam, bDiam, 0, 90);
                // 底部切入对话小尾巴
                bubble.AddLine(bubbleRect.Right - bDiam, bubbleRect.Bottom, bounds.Left + bounds.Width * 0.40f, bubbleRect.Bottom);
                bubble.AddLine(bounds.Left + bounds.Width * 0.40f, bubbleRect.Bottom, bounds.Left + bounds.Width * 0.15f, bounds.Bottom - bounds.Height * 0.05f);
                bubble.AddLine(bounds.Left + bounds.Width * 0.15f, bounds.Bottom - bounds.Height * 0.05f, bounds.Left + bounds.Width * 0.22f, bubbleRect.Bottom);
                bubble.AddArc(bubbleRect.Left, bubbleRect.Bottom - bDiam, bDiam, bDiam, 90, 90);
                bubble.CloseFigure();
                g.DrawPath(pen, bubble);

                // 对话泡泡内部三点
                var midY = bubbleRect.Top + bubbleRect.Height / 2f;
                var dotR = Math.Max(1.0f, bounds.Width * 0.06f);
                g.FillEllipse(brush, bubbleRect.Left + bubbleRect.Width * 0.25f - dotR, midY - dotR, dotR * 2f, dotR * 2f);
                g.FillEllipse(brush, bubbleRect.Left + bubbleRect.Width * 0.50f - dotR, midY - dotR, dotR * 2f, dotR * 2f);
                g.FillEllipse(brush, bubbleRect.Left + bubbleRect.Width * 0.75f - dotR, midY - dotR, dotR * 2f, dotR * 2f);
                break;
        }
    }
}

/// <summary>
/// 屏幕浮层通知度量参数。
/// 仅依据目标窗口的物理分辨率缩放；同一分辨率在所有 DPI 下保持相同像素尺寸。
/// </summary>
public sealed class NotificationMetrics
{
    public static readonly NotificationMetrics Default = new(1.0f);

    public float Scale { get; }
    public float CardWidth { get; }
    public float MinCardHeight { get; }
    public float CornerRadius { get; }
    public float CardSpacing { get; }
    public float PaddingLeft { get; }
    public float PaddingRight { get; }
    public float PaddingTop { get; }
    public float PaddingBottom { get; }
    public float IconSize { get; }
    public float IconGap { get; }
    public float ProgressBarThickness { get; }
    public float OutlineThickness { get; }
    public float IconStrokeThickness { get; }
    public float FontSize { get; }
    public float BaseTopOffset { get; }
    public float AvailableTextWidth { get; }

    public static NotificationMetrics FromPhysicalResolution(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return Default;
        // 固定 1.0 倍：卡片永远是 300×42、13px 字，不再随分辨率放大。
        // 以前这里按 Math.Max(1, min(W/1920, H/1080)) 随分辨率放大——2K 屏 1.33 倍、
        // 4K 屏 2 倍，一张卡片能吃掉四分之一屏宽，玩家反馈「比之前大很多」。
        // 保持恒定尺寸后，任何分辨率、任何游戏窗口都是同一个小方块，定位仍按游戏窗口居中。
        return new NotificationMetrics(1f);
    }

    public NotificationMetrics(float scale = 1.0f)
    {
        Scale = Math.Max(0.5f, scale);
        CardWidth = OverlayNotificationTheme.CardWidth * Scale;
        MinCardHeight = OverlayNotificationTheme.MinCardHeight * Scale;
        CornerRadius = 7f * Scale;
        CardSpacing = 8f * Scale;
        PaddingLeft = 11f * Scale;
        PaddingRight = 11f * Scale;
        PaddingTop = 10f * Scale;
        PaddingBottom = 10f * Scale;
        IconSize = 18f * Scale;
        IconGap = 8f * Scale;
        ProgressBarThickness = 3f * Scale;
        OutlineThickness = Math.Max(1.0f, 1.4f * Scale);
        IconStrokeThickness = Math.Max(1.2f, 1.6f * Scale);
        FontSize = 13f * Scale;
        BaseTopOffset = 10f * Scale;
        AvailableTextWidth = CardWidth - (PaddingLeft + IconSize + IconGap + PaddingRight);
    }
}

/// <summary>
/// 提示配色组合（轮廓色、填充色、字体与图标色）。
/// </summary>
public sealed record NotificationColorScheme(
    Color OutlineColor,
    Color FillColor,
    Color TextColor);
