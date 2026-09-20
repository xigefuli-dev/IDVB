namespace IdentityVisionBridge.PluginSdk;

public abstract record PluginHostEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string> Extensions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record MatchStateChangedEvent : PluginHostEvent
{
    public required string State { get; init; }

    public string? Mode { get; init; }
}

public sealed record SessionStateChangedEvent : PluginHostEvent
{
    public required string State { get; init; }

    public string? SessionId { get; init; }
}

public sealed record MapLockChangedEvent : PluginHostEvent
{
    public bool IsLocked { get; init; }

    public string? MapId { get; init; }

    public string? FloorId { get; init; }

    public bool HasTrustedAlignment { get; init; }
}

public sealed record SurveyStateChangedEvent : PluginHostEvent
{
    public required string State { get; init; }

    public string? Detail { get; init; }
}

public sealed record HostConfigurationChangedEvent : PluginHostEvent
{
    public required IReadOnlyList<string> ChangedSections { get; init; }
}

public sealed record PluginInputEvent
{
    public required string BindingId { get; init; }

    public required PluginInputTransition Transition { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum PluginInputTransition
{
    Pressed,
    Released
}
