namespace IdentityVisionBridge.PluginRuntime;

public sealed record PluginCatalog
{
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<PluginCatalogEntry> Plugins { get; init; } = [];
}

public sealed record PluginCatalogEntry
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string PublisherId { get; init; }

    public required string PublisherName { get; init; }

    public string? PublisherKeyId { get; init; }

    public string? ActiveVersion { get; init; }

    public string? PendingVersion { get; init; }

    public IReadOnlyList<string> PreviousVersions { get; init; } = [];

    public bool Enabled { get; init; }

    public IReadOnlyList<string> GrantedCapabilities { get; init; } = [];

    public bool CapabilityApprovalRequired { get; init; }

    public string? QuarantineReason { get; init; }

    public bool PendingDelete { get; init; }

    public bool DeleteDataOnUninstall { get; init; }

    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TrustedPublisherCatalog
{
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<TrustedPublisher> Publishers { get; init; } = [];
}

public sealed record TrustedPublisher
{
    public required string PublisherId { get; init; }

    public required string PublisherName { get; init; }

    public required string KeyId { get; init; }

    public DateTimeOffset TrustedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PluginInstallApproval
{
    public bool TrustPublisher { get; init; }

    public IReadOnlySet<string> ApprovedCapabilities { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool AllowDeveloperDowngrade { get; init; }
}

public sealed record PluginInstallResult
{
    public required PluginCatalogEntry CatalogEntry { get; init; }

    public required string InstalledVersion { get; init; }

    public required bool RequiresRestart { get; init; }

    public required bool PublisherWasNewlyTrusted { get; init; }
}
