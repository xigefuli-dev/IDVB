namespace IdentityVisionBridge.PluginRuntime;

public sealed record ThirdPartyPluginStatus
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    public required ThirdPartyPluginState State { get; init; }

    public string? Detail { get; init; }
}

public enum ThirdPartyPluginState
{
    Disabled,
    Starting,
    Running,
    Stopping,
    Quarantined,
    Incompatible,
    PendingRestart
}

public sealed record PluginSafeModeState
{
    public bool IsActive { get; init; }

    public int ConsecutiveAbnormalExits { get; init; }

    public string? SuspectedPluginId { get; init; }
}

internal sealed record PluginLoadingMarker
{
    public required string PluginId { get; init; }

    public required string Version { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record PluginSessionMarker
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> EnabledPluginIds { get; init; } = [];
}

internal sealed record PluginCrashState
{
    public int ConsecutiveAbnormalExits { get; init; }
}
