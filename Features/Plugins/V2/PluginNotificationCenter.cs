using IDVBuff.Core.Contracts;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Features.Plugins.V2;

public sealed record HostedPluginNotification(
    string PluginId,
    PluginNotification Notification,
    DateTimeOffset PostedAt);

public sealed class PluginNotificationCenter(IOverlayNotificationService? overlay = null)
{
    public event EventHandler<HostedPluginNotification>? NotificationPosted;

    public void Post(string pluginId, PluginNotification notification)
    {
        var type = notification.Severity switch
        {
            PluginNotificationSeverity.Error => OverlayNotificationType.Error,
            PluginNotificationSeverity.Warning => OverlayNotificationType.Warning,
            _ => OverlayNotificationType.Notice
        };
        overlay?.Post(type, $"{notification.Title}\n{notification.Message}", duration: notification.Duration);
        NotificationPosted?.Invoke(
            this,
            new HostedPluginNotification(pluginId, notification, DateTimeOffset.UtcNow));
    }
}
