using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Features.Plugins.V2;

public sealed record HostedPluginNotification(
    string PluginId,
    PluginNotification Notification,
    DateTimeOffset PostedAt);

public sealed class PluginNotificationCenter
{
    public event EventHandler<HostedPluginNotification>? NotificationPosted;

    public void Post(string pluginId, PluginNotification notification) =>
        NotificationPosted?.Invoke(
            this,
            new HostedPluginNotification(pluginId, notification, DateTimeOffset.UtcNow));
}
