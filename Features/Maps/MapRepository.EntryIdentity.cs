namespace IDVBuff.Features.Maps;

public sealed partial class MapDraft
{
    internal string? EntryIdentitySourceDirectory { get; set; }
}

public sealed partial class MapRepository
{
    private bool HasValidatedDraftEntryIdentity(MapDraft draft, string floorKey)
    {
        var asset = draft.Floors.FirstOrDefault(floor => floor.Key == floorKey)?.EntryIdentityAsset;
        if (asset is null) return false;
        var directory = draft.EntryIdentitySourceDirectory
            ?? (draft.Id is { } mapId ? GetMapDirectory(mapId) : null)
            ?? throw new InvalidDataException("入口身份资源缺少来源目录。");
        var packageId = draft.SourcePackageMapId ?? draft.Id
            ?? throw new InvalidDataException("入口身份资源缺少来源地图 ID。");
        MapEntryIdentityResources.ValidateFiles(directory, asset, packageId, floorKey);
        // Save validates source hash, dimensions and crop again after creating the new floor assets.
        return true;
    }

    private void CopyEntryIdentityAssets(MapDraft draft, MapRecord record, string stagingDirectory)
    {
        foreach (var floor in record.Floors.Where(floor => floor.EntryIdentityAsset is not null))
        {
            var asset = floor.EntryIdentityAsset!;
            var sourceDirectory = draft.EntryIdentitySourceDirectory ?? GetMapDirectory(record.Id);
            MapEntryIdentityResources.ValidateFiles(sourceDirectory, asset,
                record.SourcePackageMapId ?? record.Id, floor.Key);
            foreach (var fileName in MapEntryIdentityResources.EnumerateFileNames(asset))
            {
                var source = GetSafeMapFilePath(sourceDirectory, fileName);
                var target = GetSafeMapFilePath(stagingDirectory, fileName);
                if (File.Exists(target))
                    throw new InvalidDataException("入口身份资源与楼层资源文件名冲突。");
                File.Copy(source, target);
            }
            // Validate against the newly saved image/crop, never a stale draft hash.
            using var verified = MapEntryIdentityResources.Load(stagingDirectory, record, floor);
        }
    }
}
