using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record MapStructureLineReference(string Path, string SourceKind, string GenerationIdentity);

public sealed partial class MapRepository
{
    internal MapStructureLineReference? ResolveStructureLineReference(MapRecord map, string floorKey)
    {
        var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.Ordinal));
        if (floor is null) return null;
        if (floor.EntryIdentityAsset is { } asset)
        {
            // A declared author resource is authoritative. Invalid bindings must
            // fail rather than silently switching to pixels from the guide image.
            using var resource = MapEntryIdentityResources.Load(GetMapDirectory(map.Id), map, floor);
            return new(GetSafeMapFilePath(GetMapDirectory(map.Id), asset.KnownBoundaryFileName),
                "EntryKnownBoundary", EntryStructureGeneration(asset));
        }
        return HasPrebuiltStructureLine(map, floorKey)
            ? new(GetPrebuiltStructureLinePath(map, floorKey), "PrebuiltStructureLine",
                Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(floor.PrebuiltStructureLine!))
            : null;
    }

    internal static string EntryStructureGeneration(MapEntryIdentityAsset? asset) => asset is null
        ? string.Empty
        : $"entry-v{asset.SchemaVersion}|{asset.SourceImageSha256}|{asset.SourceWidth}x{asset.SourceHeight}"
            + $"|{asset.PassportFileName}|{asset.KnownBoundaryFileName}|{asset.UnknownMaskFileName}";

    internal Mat? LoadStructureReferenceUnknown(MapRecord map, string floorKey)
    {
        var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.Ordinal));
        if (floor?.EntryIdentityAsset is null) return null;
        using var resource = MapEntryIdentityResources.Load(GetMapDirectory(map.Id), map, floor);
        // Force validation of both geometry channels before either alignment cache uses them.
        _ = resource.KnownBoundary;
        return resource.UnknownMask.Clone();
    }

    private bool HasValidatedEntryStructure(MapRecord map, FloorDefinition floor)
    {
        if (floor.EntryIdentityAsset is null) return false;
        using var resource = MapEntryIdentityResources.Load(GetMapDirectory(map.Id), map, floor);
        _ = resource.KnownBoundary;
        _ = resource.UnknownMask;
        return true;
    }
}
