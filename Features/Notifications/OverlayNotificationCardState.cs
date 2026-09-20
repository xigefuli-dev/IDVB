using System.Drawing;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 屏幕浮层通知卡片的视觉渲染与动画状态机。
/// 负责单张卡片即时位置、渐变透明度、平滑动画合成与重定向（Retargeting）。
/// </summary>
public sealed class OverlayNotificationCardState
{
    private static readonly StringFormat TextFormat = new(StringFormatFlags.LineLimit)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.EllipsisWord
    };

    public OverlayNotificationCardState(
        IOverlayNotification notification,
        float targetY,
        long now,
        NotificationMetrics? metrics = null)
    {
        Notification = notification;
        var m = metrics ?? NotificationMetrics.Default;
        MeasuredHeight = m.MinCardHeight;
        DisplayStartTick = now;

        // 入场动画：透明度从 0 到 1，位置平滑从上方下滑进入，确保不超出画布上边界
        StartAlpha = 0f;
        TargetAlpha = 1f;
        AlphaStartTick = now;
        AlphaDurationMs = 260f;

        StartY = Math.Max(0f, targetY - 24f * m.Scale);
        TargetY = targetY;
        MoveStartTick = now;
        MoveDurationMs = 320f;
    }

    public IOverlayNotification Notification { get; }

    /// <summary>卡片实际开始在屏幕展示的时间戳（Environment.TickCount64）。</summary>
    public long DisplayStartTick { get; set; }

    /// <summary>
    /// 将卡片实际动画与展示起始基准对齐至指定帧时间戳。
    /// </summary>
    public void AlignTimeBase(long now)
    {
        DisplayStartTick = now;
        AlphaStartTick = now;
        MoveStartTick = now;
    }

    /// <summary>要点 A：动态计算的卡片高度（因文本换行而扩展）。</summary>
    public float MeasuredHeight { get; private set; }

    public float StartY { get; private set; }
    public float TargetY { get; private set; }
    public long MoveStartTick { get; private set; }
    public float MoveDurationMs { get; private set; }

    public float StartAlpha { get; private set; }
    public float TargetAlpha { get; private set; }
    public long AlphaStartTick { get; private set; }
    public float AlphaDurationMs { get; private set; }

    public bool IsExiting { get; private set; }
    public bool IsFullyExited { get; private set; }

    /// <summary>
    /// 获取当前时间点的插值 Y 坐标。
    /// 入场与重排采用 EaseOutCubic 减速缓动，退场采用 EaseIn 加速上移。
    /// </summary>
    public float GetInterpolatedY(long now)
    {
        if (MoveDurationMs <= 0f) return TargetY;
        var t = Math.Clamp((now - MoveStartTick) / MoveDurationMs, 0f, 1f);
        var eased = IsExiting
            ? (float)Math.Pow(t, 2.2) // EaseIn 加速上移
            : 1f - (float)Math.Pow(1f - t, 3); // EaseOutCubic 平滑进场与重排
        return StartY + (TargetY - StartY) * eased;
    }

    /// <summary>
    /// 获取当前时间点的插值透明度 (0.0 ~ 1.0)。
    /// </summary>
    public float GetInterpolatedAlpha(long now)
    {
        if (AlphaDurationMs <= 0f) return TargetAlpha;
        var t = Math.Clamp((now - AlphaStartTick) / AlphaDurationMs, 0f, 1f);
        var eased = IsExiting
            ? t * t // EaseIn 加速淡出
            : 1f - (float)Math.Pow(1f - t, 2); // EaseOut 平滑淡入
        return Math.Clamp(StartAlpha + (TargetAlpha - StartAlpha) * eased, 0f, 1f);
    }

    /// <summary>
    /// 合成重定向位移动画（在移动过程中槽位变化，平滑过渡至新位置）。
    /// </summary>
    public void RetargetPosition(float newTargetY, long now)
    {
        if (IsExiting || Math.Abs(TargetY - newTargetY) < 0.2f) return;
        StartY = GetInterpolatedY(now);
        TargetY = newTargetY;
        MoveStartTick = now;
        MoveDurationMs = 260f;
    }

    /// <summary>
    /// 开始退出动画（向上轻微浮动同时加速淡出）。
    /// </summary>
    public void BeginExit(long now, NotificationMetrics? metrics = null)
    {
        if (IsExiting) return;
        IsExiting = true;
        var m = metrics ?? NotificationMetrics.Default;

        var currentY = GetInterpolatedY(now);
        StartY = currentY;
        TargetY = Math.Max(0f, currentY - 24f * m.Scale);
        MoveStartTick = now;
        MoveDurationMs = 240f;

        StartAlpha = GetInterpolatedAlpha(now);
        TargetAlpha = 0f;
        AlphaStartTick = now;
        AlphaDurationMs = 240f;
    }

    /// <summary>
    /// 检查并更新动画生命周期状态。
    /// </summary>
    public void UpdateLifecycle(long now)
    {
        if (IsExiting && !IsFullyExited)
        {
            if (MoveStartTick > now) MoveStartTick = now;
            if (AlphaStartTick > now) AlphaStartTick = now;

            if (now >= MoveStartTick + (long)MoveDurationMs &&
                now >= AlphaStartTick + (long)AlphaDurationMs)
            {
                IsFullyExited = true;
            }
        }
    }

    /// <summary>
    /// 要点 A：依据文字内容及是否换行动态测量卡片高度。
    /// </summary>
    public void Measure(Graphics g, Font font, NotificationMetrics? metrics = null)
    {
        var m = metrics ?? NotificationMetrics.Default;
        var availableWidth = m.AvailableTextWidth;
        var size = g.MeasureString(
            Notification.Message,
            font,
            new SizeF(availableWidth, 10000f),
            TextFormat);

        var contentHeight = Math.Max(18f * m.Scale, size.Height);
        var hasProgress = Notification.Progress.HasValue;
        var extraProgress = hasProgress ? m.ProgressBarThickness : 0f;

        var computedHeight = m.PaddingTop
                             + contentHeight
                             + m.PaddingBottom
                             + extraProgress;

        MeasuredHeight = Math.Max(m.MinCardHeight, (float)Math.Ceiling(computedHeight));
    }
}
