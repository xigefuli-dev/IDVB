namespace IdentityVisionBridge.PluginSdk;

public static class PluginCapabilityIds
{
    public const string HostEventsRead = "host.events.read";
    public const string InputBindings = "input.bindings";
    public const string CaptureScreenshot = "capture.screenshot";
    public const string StoragePrivate = "storage.private";
    public const string NotificationsPost = "notifications.post";

    public static IReadOnlySet<string> PublicV1 { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        HostEventsRead,
        InputBindings,
        CaptureScreenshot,
        StoragePrivate,
        NotificationsPost
    };
}

public interface IPluginCapability
{
}

public interface IHostEventsCapability : IPluginCapability
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : PluginHostEvent;
}

public interface IInputBindingsCapability : IPluginCapability
{
    IDisposable Subscribe(string bindingId, Func<PluginInputEvent, CancellationToken, ValueTask> handler);
}

public interface IScreenshotCapability : IPluginCapability
{
    ValueTask<PluginScreenshotResult> CaptureAsync(CancellationToken cancellationToken);
}

public interface IPluginStorageCapability : IPluginCapability
{
    string RootDirectory { get; }
}

public interface IPluginNotificationsCapability : IPluginCapability
{
    ValueTask PostAsync(PluginNotification notification, CancellationToken cancellationToken);
}

public sealed record PluginScreenshotResult
{
    public required bool Succeeded { get; init; }

    public byte[]? PngBytes { get; init; }

    public string? ErrorCode { get; init; }

    public string? UserMessage { get; init; }
}

public sealed record PluginNotification
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public PluginNotificationSeverity Severity { get; init; }
}

public enum PluginNotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}
