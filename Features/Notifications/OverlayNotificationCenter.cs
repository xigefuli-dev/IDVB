using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// 专属通知事件中心静态外观门面。
/// 类似 RealtimePerformanceTracker / RealtimePerformanceOverlay，
/// 允许整个应用程序、插件或后台管线在任意位置直接订阅通知事件或发布屏幕浮层通知。
/// </summary>
public static class OverlayNotificationCenter
{
    private static readonly object Gate = new();
    private static OverlayNotificationQueue? _sharedQueue;
    private static IOverlayNotificationService? _service;

    /// <summary>
    /// 专属通知事件。每当有新通知产生时触发。
    /// </summary>
    public static event EventHandler<OverlayNotificationEventArgs>? NotificationPosted;

    /// <summary>
    /// 请求关闭指定通知的事件。
    /// </summary>
    public static event Action<string>? DismissRequested;

    /// <summary>
    /// 绑定或替换当前活动的通知服务实例（通常在 DI 容器构建后注入）。
    /// </summary>
    public static void Initialize(IOverlayNotificationService service)
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                _service.NotificationPosted -= OnServiceNotificationPosted;
                _service.DismissRequested -= OnServiceDismissRequested;
                if (!ReferenceEquals(_service, service) && _service is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { }
                }
            }
            _service = service;
            if (_service is not null)
            {
                _service.NotificationPosted += OnServiceNotificationPosted;
                _service.DismissRequested += OnServiceDismissRequested;
            }
        }
    }

    /// <summary>
    /// 发布一个屏幕浮层通知。
    /// </summary>
    /// <param name="type">通知类型（错误、警告、通知）。</param>
    /// <param name="message">提示内容。</param>
    /// <param name="progress">进度值（0.0 ~ 1.0，可选）。</param>
    /// <param name="duration">显示时长（默认 5 秒；可传自定义时长或 Timeout.InfiniteTimeSpan）。</param>
    /// <returns>通知控制器对象。</returns>
    public static IOverlayNotification Post(
        OverlayNotificationType type,
        string message,
        double? progress = null,
        TimeSpan? duration = null)
    {
        EnsureInitialized();
        return _service!.Post(type, message, progress, duration);
    }

    /// <summary>快捷发布错误通知（红色系，默认 5 秒后自动消失）。</summary>
    public static IOverlayNotification Error(string message, double? progress = null, TimeSpan? duration = null) =>
        Post(OverlayNotificationType.Error, message, progress, duration);

    /// <summary>快捷发布警告通知（橙色系，默认 5 秒后自动消失）。</summary>
    public static IOverlayNotification Warning(string message, double? progress = null, TimeSpan? duration = null) =>
        Post(OverlayNotificationType.Warning, message, progress, duration);

    /// <summary>快捷发布一般通知（绿色系，默认 5 秒后自动消失）。</summary>
    public static IOverlayNotification Notice(string message, double? progress = null, TimeSpan? duration = null) =>
        Post(OverlayNotificationType.Notice, message, progress, duration);

    /// <summary>关闭指定 ID 的通知。</summary>
    public static void Dismiss(string id)
    {
        EnsureInitialized();
        _service!.Dismiss(id);
    }

    /// <summary>请求关闭指定 ID 的通知（触发 DismissRequested 事件并执行关闭）。</summary>
    public static void RequestDismiss(string id) => Dismiss(id);

    /// <summary>清空所有活动与等待通知。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _service?.Clear();
            _sharedQueue?.Clear();
        }
    }

    /// <summary>更新游戏窗口边界，使通知窗口自适应跟随居中。</summary>
    public static void UpdateGameBounds(MapScreenRect bounds)
    {
        lock (Gate)
        {
            if (_service is OverlayNotificationService concreteService)
            {
                concreteService.UpdateGameBounds(bounds);
            }
            else
            {
                OverlayNotificationWindow.Instance?.UpdateGameBounds(bounds);
            }
        }
    }

    private static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_service is not null) return;

            _sharedQueue ??= new OverlayNotificationQueue();
            var concrete = new OverlayNotificationService(_sharedQueue);
            concrete.NotificationPosted += OnServiceNotificationPosted;
            concrete.DismissRequested += OnServiceDismissRequested;
            _service = concrete;
        }
    }

    private static void OnServiceNotificationPosted(object? sender, OverlayNotificationEventArgs e)
    {
        NotificationPosted?.Invoke(sender, e);
    }

    private static void OnServiceDismissRequested(string id)
    {
        DismissRequested?.Invoke(id);
    }
}
