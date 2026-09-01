namespace IDVBuff.Features.Maps;

public enum MapAcquisitionKind
{
    Local,
    ImportedPackage,
    Subscription
}

public sealed partial class MapRecord
{
    public string Source { get; set; } = "manual";
    public Guid? SourceProjectId { get; set; }
    public long? SourceProjectRevision { get; set; }
    public string? SourceVisualSha256 { get; set; }
    public string? SourceStructureSha256 { get; set; }
    /// <summary>Local provenance only; portable IDVM writers intentionally omit it.</summary>
    public MapAcquisitionKind AcquisitionKind { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string? SubscriptionPublisherHandle { get; set; }
    public bool SubscriptionPublisherIsOfficial { get; set; }
    public bool SubscriptionPublisherIsBuilder { get; set; }
    public string? SubscriptionPublisherKeyId { get; set; }
    public string? SubscriptionVersion { get; set; }
}

public sealed partial class MapDraft
{
    public string Source { get; set; } = "manual";
    public Guid? SourceProjectId { get; set; }
    public long? SourceProjectRevision { get; set; }
    public string? SourceVisualSha256 { get; set; }
    public string? SourceStructureSha256 { get; set; }
    public MapAcquisitionKind AcquisitionKind { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string? SubscriptionPublisherHandle { get; set; }
    public bool SubscriptionPublisherIsOfficial { get; set; }
    public bool SubscriptionPublisherIsBuilder { get; set; }
    public string? SubscriptionPublisherKeyId { get; set; }
    public string? SubscriptionVersion { get; set; }
}
