using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 浮层通知实例实现。支持线程安全地更新进度、文本与生命周期状态。
/// </summary>
public sealed class OverlayNotificationItem : IOverlayNotification
{
    private readonly object _gate = new();
    private string _message;
    private double? _progress;
    private bool _isDismissed;
    private TimeSpan? _displayLifetime;

    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    public OverlayNotificationItem(
        OverlayNotificationType type,
        string message,
        double? progress = null,
        TimeSpan? duration = null)
    {
        Id = Guid.NewGuid().ToString("N");
        Type = type;
        _message = message ?? string.Empty;
        _progress = progress.HasValue ? Math.Clamp(progress.Value, 0.0, 1.0) : null;
        Duration = duration ?? DefaultDuration;
        _displayLifetime = Duration == Timeout.InfiniteTimeSpan ? null : Duration;
        CreatedAt = DateTimeOffset.UtcNow;
        ExpireTime = Duration == Timeout.InfiniteTimeSpan ? null : CreatedAt + Duration;
    }

    public string Id { get; }
    public OverlayNotificationType Type { get; }
    public DateTimeOffset CreatedAt { get; }
    public TimeSpan Duration { get; }
    public DateTimeOffset? ExpireTime { get; private set; }

    public string Message
    {
        get { lock (_gate) return _message; }
    }

    public double? Progress
    {
        get { lock (_gate) return _progress; }
    }

    public bool IsDismissed
    {
        get { lock (_gate) return _isDismissed; }
    }

    public event Action<IOverlayNotification>? Updated;
    public event Action<IOverlayNotification>? Dismissed;

    internal void StartDisplayLifetime(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_isDismissed)
                ExpireTime = _displayLifetime.HasValue ? now + _displayLifetime.Value : null;
        }
    }

    public void UpdateProgress(double progress, string? newMessage = null)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_isDismissed) return;
            var clamped = Math.Clamp(progress, 0.0, 1.0);
            if (_progress != clamped)
            {
                _progress = clamped;
                changed = true;
            }
            if (newMessage is not null && _message != newMessage)
            {
                _message = newMessage;
                changed = true;
            }
            // 达到 100% 进度时，默认 1.5 秒后优雅退出
            if (clamped >= 1.0)
            {
                _displayLifetime = TimeSpan.FromSeconds(1.5);
                ExpireTime = DateTimeOffset.UtcNow.AddSeconds(1.5);
            }
        }

        if (changed)
            Updated?.Invoke(this);
    }

    public void UpdateMessage(string message)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_isDismissed) return;
            var text = message ?? string.Empty;
            if (_message != text)
            {
                _message = text;
                changed = true;
            }
        }

        if (changed)
            Updated?.Invoke(this);
    }

    public void Dismiss()
    {
        lock (_gate)
        {
            if (_isDismissed) return;
            _isDismissed = true;
        }

        Dismissed?.Invoke(this);
    }
}
