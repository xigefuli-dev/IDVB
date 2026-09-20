using System.Drawing;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 屏幕浮层通知队列管理器。
/// 管理最多 5 个并发活动卡片，执行溢出排队、要点 A 动态高度排版、重叠规避以及平滑位移合成。
/// </summary>
public sealed class OverlayNotificationQueue
{
    public const int MaxConcurrentCards = 5;
    public const float BaseTopOffset = 10f;

    private readonly object _gate = new();
    private readonly List<OverlayNotificationCardState> _activeCards = [];
    private readonly Queue<IOverlayNotification> _pendingQueue = new();
    private NotificationMetrics _currentMetrics = NotificationMetrics.Default;
    private Font? _measureFont;

    public event Action? StateChanged;

    /// <summary>当前是否有卡片正在显示或排队中。</summary>
    public bool HasVisibleItems
    {
        get
        {
            lock (_gate)
                return _activeCards.Count > 0 || _pendingQueue.Count > 0;
        }
    }

    /// <summary>获取当前活动卡片只读副本（用于渲染）。</summary>
    public List<OverlayNotificationCardState> GetSnapshot()
    {
        lock (_gate)
            return [.. _activeCards];
    }

    /// <summary>
    /// 入队一条新通知。若当前活动数量小于 5 则立即入场展示，否则进入等待队列。
    /// </summary>
    public void Enqueue(IOverlayNotification notification)
    {
        if (notification is null) return;

        lock (_gate)
        {
            notification.Dismissed += OnNotificationDismissed;
            notification.Updated += OnNotificationUpdated;

            if (_activeCards.Count < MaxConcurrentCards)
            {
                PromoteToActiveCore(notification, Environment.TickCount64, _currentMetrics);
            }
            else
            {
                _pendingQueue.Enqueue(notification);
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// 关闭指定 ID 的通知。
    /// </summary>
    public void Dismiss(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var card = _activeCards.Find(c => c.Notification.Id == id);
            if (card is not null && !card.IsExiting)
            {
                card.BeginExit(now, _currentMetrics);
                card.Notification.Dismiss();
                RecalculateLayoutCore(now, _currentMetrics);
            }
            else
            {
                foreach (var pending in _pendingQueue)
                {
                    if (pending.Id == id)
                    {
                        pending.Dismiss();
                    }
                }
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// 清空所有当前活动与等待中的通知。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var card in _activeCards)
            {
                card.BeginExit(now, _currentMetrics);
                card.Notification.Dismiss();
            }
            _pendingQueue.Clear();
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// 帧更新：处理卡片超时、淡出清理、候补出队与堆叠重布局。
    /// 返回 true 表示仍在动画中（需要继续驱动高频重绘），false 表示已处于静止稳态。
    /// </summary>
    public bool UpdateFrame(Graphics measureGraphics, long now, NotificationMetrics? metrics = null)
    {
        var m = metrics ?? _currentMetrics;
        _currentMetrics = m;
        bool isAnimating = false;
        bool layoutChanged = false;

        lock (_gate)
        {
            // 0. 检查手动关闭通知
            foreach (var card in _activeCards)
            {
                if (!card.IsExiting && card.Notification.IsDismissed)
                {
                    card.BeginExit(now, m);
                    layoutChanged = true;
                }
            }

            // 1. 检查自动超时退出（默认 5 秒后自动消失，或者由 item.ExpireTime 决定）
            var utcNow = DateTimeOffset.UtcNow;
            foreach (var card in _activeCards)
            {
                if (card.IsExiting) continue;

                // 确保卡片时间基准与驱动时间对齐
                if (card.DisplayStartTick == 0 || card.DisplayStartTick > now)
                {
                    card.AlignTimeBase(now);
                }

                bool expired = false;

                if (card.Notification is OverlayNotificationItem item && item.ExpireTime.HasValue)
                {
                    if (utcNow >= item.ExpireTime.Value)
                    {
                        expired = true;
                    }
                }

                if (!expired && card.Notification.Duration != Timeout.InfiniteTimeSpan && card.Notification.Duration > TimeSpan.Zero)
                {
                    var elapsedMs = now - card.DisplayStartTick;
                    if (elapsedMs >= card.Notification.Duration.TotalMilliseconds)
                    {
                        expired = true;
                    }
                }

                if (expired)
                {
                    card.BeginExit(now, m);
                    layoutChanged = true;
                }
            }

            // 2. 清理完全淡出完毕的卡片
            for (int i = _activeCards.Count - 1; i >= 0; i--)
            {
                var card = _activeCards[i];
                card.UpdateLifecycle(now);
                if (card.IsFullyExited)
                {
                    _activeCards.RemoveAt(i);
                    layoutChanged = true;
                }
            }

            // 3. 若当前活动卡片少于 5 个且有排队，出队补位（确保最多同时显示 5 个）
            while (_activeCards.Count < MaxConcurrentCards && _pendingQueue.Count > 0)
            {
                var next = _pendingQueue.Dequeue();
                if (!next.IsDismissed)
                {
                    PromoteToActiveCore(next, now, m);
                    layoutChanged = true;
                }
            }

            // 4. 要点 A：依据当前 DPI/分辨率 Metrics 对所有活动卡片进行文本与换行高度测量
            if (_measureFont is null || Math.Abs(_measureFont.Size - m.FontSize) > 0.1f)
            {
                _measureFont?.Dispose();
                _measureFont = new Font("Microsoft YaHei UI", m.FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }

            foreach (var card in _activeCards)
            {
                var oldHeight = card.MeasuredHeight;
                card.Measure(measureGraphics, _measureFont, m);
                if (Math.Abs(card.MeasuredHeight - oldHeight) > 0.5f)
                {
                    layoutChanged = true;
                }
            }

            // 5. 堆叠重排与动画合成重定向（A消失->B到A, C到B；B先消失->C到B；动态高度累加）
            if (layoutChanged)
            {
                RecalculateLayoutCore(now, m);
            }

            // 6. 检测当前是否仍有动画正在进行
            foreach (var card in _activeCards)
            {
                if (card.IsExiting)
                {
                    if (!card.IsFullyExited)
                    {
                        isAnimating = true;
                        break;
                    }
                    continue;
                }

                var currentY = card.GetInterpolatedY(now);
                var currentAlpha = card.GetInterpolatedAlpha(now);

                if (Math.Abs(currentY - card.TargetY) > 0.5f ||
                    Math.Abs(currentAlpha - card.TargetAlpha) > 0.01f)
                {
                    isAnimating = true;
                    break;
                }
            }
        }

        return isAnimating;
    }

    private void PromoteToActiveCore(IOverlayNotification notification, long now, NotificationMetrics m)
    {
        // 计算新卡片入场的初始 TargetY（排在当前所有非退出卡片下方）
        float nextY = m.BaseTopOffset;
        for (int i = 0; i < _activeCards.Count; i++)
        {
            if (!_activeCards[i].IsExiting)
            {
                nextY += _activeCards[i].MeasuredHeight + m.CardSpacing;
            }
        }

        var state = new OverlayNotificationCardState(notification, nextY, now, m);
        _activeCards.Add(state);
    }

    private void RecalculateLayoutCore(long now, NotificationMetrics m)
    {
        // 按照队列先后顺序，仅为未退出的卡片重新分配 TargetY。
        // 已退出的卡片留在原地淡出，不再参与占位，后续卡片立即启动位移合成。
        float currentY = m.BaseTopOffset;
        for (int i = 0; i < _activeCards.Count; i++)
        {
            var card = _activeCards[i];
            if (card.IsExiting) continue;

            // 动态累加：注意要点 A，每个卡片高度不同
            card.RetargetPosition(currentY, now);
            currentY += card.MeasuredHeight + m.CardSpacing;
        }
    }

    private void OnNotificationDismissed(IOverlayNotification notification)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var card = _activeCards.Find(c => c.Notification.Id == notification.Id);
            if (card is not null && !card.IsExiting)
            {
                card.BeginExit(now, _currentMetrics);
                RecalculateLayoutCore(now, _currentMetrics);
            }
        }

        StateChanged?.Invoke();
    }

    private void OnNotificationUpdated(IOverlayNotification notification)
    {
        StateChanged?.Invoke();
    }
}
