namespace IDVBuff.Features.Maps;

/// <summary>
/// Optional source-registered entrance geometry. The dimensions describe its author
/// coordinate canvas, not the JSON file. Masks have separate, explicit semantics.
/// </summary>
public sealed class MapEntryIdentityAsset
{
    public int SchemaVersion { get; set; } = 1;
    public string PassportFileName { get; set; } = string.Empty;
    public string CleanMaskFileName { get; set; } = string.Empty;
    public string KnownBoundaryFileName { get; set; } = string.Empty;
    public string UnknownMaskFileName { get; set; } = string.Empty;
    public string AuthorValidationMaskFileName { get; set; } = string.Empty;
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public string SourceImageSha256 { get; set; } = string.Empty;

    public MapEntryIdentityAsset Clone() => (MapEntryIdentityAsset)MemberwiseClone();
}

public sealed partial class FloorDefinition
{
    public MapEntryIdentityAsset? EntryIdentityAsset { get; set; }
}
