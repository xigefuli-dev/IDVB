namespace IdentityVisionBridge.PluginSdk;

public static class PluginCapabilityIds
{
    public const string HostEventsRead = "host.events.read";
    public const string InputBindings = "input.bindings";
    public const string CaptureScreenshot = "capture.screenshot";
    public const string StoragePrivate = "storage.private";
    public const string NotificationsPost = "notifications.post";
    public const string HostStateRead = "host.state.read";
    public const string HostScanRun = "host.scan.run";
    public const string HostAlignmentRun = "host.alignment.run";
    public const string VisionMapsRead = "vision.maps.read";
    public const string VisionScan = "vision.scan";
    public const string VisionAlign = "vision.align";
    public const string HostMatchControl = "host.match.control";
    public const string HostMapControl = "host.map.control";
    public const string HostOverlayControl = "host.overlay.control";
    public const string HostSettingsControl = "host.settings.control";

    public static IReadOnlySet<string> PublicV1 { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        HostEventsRead,
        InputBindings,
        CaptureScreenshot,
        StoragePrivate,
        NotificationsPost,
        HostStateRead,
        HostScanRun,
        HostAlignmentRun,
        VisionMapsRead,
        VisionScan,
        VisionAlign,
        HostMatchControl,
        HostMapControl,
        HostOverlayControl,
        HostSettingsControl
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
    /// <summary>
    /// Posts a best-effort notification. The host delivers at most five posts per plugin in a
    /// rolling minute; excess posts are suppressed without throwing or stopping the plugin.
    /// Invalid text and cancelled calls still fail.
    /// </summary>
    ValueTask PostAsync(PluginNotification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Shows the registered red, yellow or green overlay notification, with caller-specified
    /// content, display duration and optional delay. Uses the same grant and limit as PostAsync.
    /// </summary>
    ValueTask NotifyAsync(PluginNotification notification, CancellationToken cancellationToken = default) =>
        PostAsync(notification, cancellationToken);
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

    /// <summary>Time visible after the card enters the overlay. Must be positive; defaults to five seconds.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before posting to the overlay. Defaults to immediate. Delayed posts are scheduled by the
    /// host and return immediately; stopping the plugin or cancelling the call token cancels delivery.
    /// </summary>
    public TimeSpan Delay { get; init; }
}

public enum PluginNotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}
