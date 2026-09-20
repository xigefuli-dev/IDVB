using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 专属浮层通知服务实现。
/// 触发专属通知事件并将通知压入屏幕浮层渲染队列。
/// </summary>
public sealed class OverlayNotificationService : IOverlayNotificationService, IDisposable
{
    private readonly OverlayNotificationQueue _queue;
    private readonly OverlayNotificationWindow _window;

    public OverlayNotificationService(
        OverlayNotificationQueue queue,
        ICaptureProtectionService? captureProtection = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _window = new OverlayNotificationWindow(_queue, captureProtection);
    }

    public event EventHandler<OverlayNotificationEventArgs>? NotificationPosted;
    public event Action<string>? DismissRequested;

    public IOverlayNotification Post(
        OverlayNotificationType type,
        string message,
        double? progress = null,
        TimeSpan? duration = null)
    {
        var item = new OverlayNotificationItem(type, message, progress, duration);
        _queue.Enqueue(item);
        NotificationPosted?.Invoke(this, new OverlayNotificationEventArgs(item));
        return item;
    }

    public IOverlayNotification Error(string message, double? progress = null, TimeSpan? duration = null) =>
        Post(OverlayNotificationType.Error, message, progress, duration);

    public IOverlayNotification Warning(string message, double? progress = null, TimeSpan? duration = null) =>
        Post(OverlayNotificationType.Warning, message, progress, duration);

    public IOverlayNotification Notice(string message, double? progress = null, TimeSpan? duration = null) =>
        Post(OverlayNotificationType.Notice, message, progress, duration);

    public void Dismiss(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _queue.Dismiss(id);
        DismissRequested?.Invoke(id);
    }

    public void RequestDismiss(string id) => Dismiss(id);

    public void Clear()
    {
        _queue.Clear();
    }

    public void UpdateGameBounds(MapScreenRect bounds)
    {
        _window.UpdateGameBounds(bounds);
    }

    public void Dispose()
    {
        _window.Dispose();
    }
}
