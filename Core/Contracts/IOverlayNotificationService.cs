namespace IDVBuff.Core.Contracts;

/// <summary>
/// 屏幕浮层通知类型。
/// 分别对应错误（红色系）、警告（橙色系）和通知（绿色系）。
/// </summary>
public enum OverlayNotificationType
{
    /// <summary>错误（红色系）</summary>
    Error,

    /// <summary>警告（橙色系）</summary>
    Warning,

    /// <summary>通知（绿色系）</summary>
    Notice
}

/// <summary>
/// 单个浮层通知实例契约。支持动态更新进度与文本，或主动关闭。
/// </summary>
public interface IOverlayNotification
{
    /// <summary>通知全局唯一标识。</summary>
    string Id { get; }

    /// <summary>通知类型。</summary>
    OverlayNotificationType Type { get; }

    /// <summary>当前显示的消息文本。</summary>
    string Message { get; }

    /// <summary>
    /// 当前进度（0.0 ~ 1.0）；若为 null 则不显示底部进度条。
    /// </summary>
    double? Progress { get; }

    /// <summary>创建时间戳。</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>展示时长；默认为 5 秒。若为 Timeout.InfiniteTimeSpan 则需通过事件或方法主动关闭。</summary>
    TimeSpan Duration { get; }

    /// <summary>是否已被关闭/正在关闭。</summary>
    bool IsDismissed { get; }

    /// <summary>更新进度数值及可选的附加文本。</summary>
    void UpdateProgress(double progress, string? newMessage = null);

    /// <summary>更新消息文本。</summary>
    void UpdateMessage(string message);

    /// <summary>手动关闭并退出当前通知。</summary>
    void Dismiss();

    /// <summary>通知内容发生更新时触发。</summary>
    event Action<IOverlayNotification>? Updated;

    /// <summary>通知关闭时触发。</summary>
    event Action<IOverlayNotification>? Dismissed;
}

/// <summary>
/// 专属通知事件参数。
/// </summary>
public sealed class OverlayNotificationEventArgs : EventArgs
{
    public OverlayNotificationEventArgs(IOverlayNotification notification)
    {
        Notification = notification ?? throw new ArgumentNullException(nameof(notification));
    }

    public IOverlayNotification Notification { get; }
}

/// <summary>
/// 专属浮层通知服务公共契约。
/// </summary>
public interface IOverlayNotificationService
{
    /// <summary>
    /// 专属通知触发事件。
    /// </summary>
    event EventHandler<OverlayNotificationEventArgs>? NotificationPosted;

    /// <summary>
    /// 请求关闭指定通知的事件。
    /// </summary>
    event Action<string>? DismissRequested;

    /// <summary>
    /// 发布一个浮层通知（默认 5 秒后自动消失，可通过 duration 自定义或通过 Dismiss/事件主动关闭）。
    /// </summary>
    /// <param name="type">通知类型（错误、警告、通知）。</param>
    /// <param name="message">提示内容。</param>
    /// <param name="progress">初始进度（0.0 ~ 1.0，可选）。</param>
    /// <param name="duration">展示时长（默认 5 秒；可传自定义时长或 Timeout.InfiniteTimeSpan）。</param>
    /// <returns>通知控制器句柄，可后续更新进度或手动关闭。</returns>
    IOverlayNotification Post(
        OverlayNotificationType type,
        string message,
        double? progress = null,
        TimeSpan? duration = null);

    /// <summary>快捷发布错误通知（默认 5 秒后自动消失）。</summary>
    IOverlayNotification Error(string message, double? progress = null, TimeSpan? duration = null);

    /// <summary>快捷发布警告通知（默认 5 秒后自动消失）。</summary>
    IOverlayNotification Warning(string message, double? progress = null, TimeSpan? duration = null);

    /// <summary>快捷发布普通通知（默认 5 秒后自动消失）。</summary>
    IOverlayNotification Notice(string message, double? progress = null, TimeSpan? duration = null);

    /// <summary>关闭指定 ID 的通知。</summary>
    void Dismiss(string id);

    /// <summary>请求关闭指定 ID 的通知（触发 DismissRequested 事件并执行关闭）。</summary>
    void RequestDismiss(string id);

    /// <summary>清空并关闭当前所有活动与排队中的通知。</summary>
    void Clear();
}
