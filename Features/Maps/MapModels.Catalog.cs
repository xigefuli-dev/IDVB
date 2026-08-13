namespace IDVBuff.Features.Maps;

internal sealed class MapCatalogDocument
{
    /// <summary>Local catalog storage schema. Version 14 canonicalizes floor recognition profiles.</summary>
    public int StorageSchemaVersion { get; set; }
    public int NextSequenceNumber { get; set; } = 1;
    /// <summary>
    /// Persisted independently of maps so an empty class remains available in the
    /// management UI. Display names are canonicalized by <see cref="MapRepository"/>.
    /// </summary>
    public List<string> Classes { get; set; } = ["S1"];
    public List<MapRecord> Maps { get; set; } = [];
}

public sealed record MapCatalogSnapshot(
    IReadOnlyList<string> Classes,
    IReadOnlyList<MapRecord> Maps);

public sealed record MapClassDeletionResult(
    string ClassName,
    int DeletedMapCount);
