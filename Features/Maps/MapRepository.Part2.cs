namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    public async Task DeleteAsync(Guid id)
    {
        await Gate.WaitAsync();
        string? stagedDeletion = null;
        try
        {
            var catalog = await ReadCatalogAsync();
            var record = catalog.Maps.SingleOrDefault(map => map.Id == id)
                ?? throw new InvalidOperationException("找不到要删除的地图。");
            var directory = GetMapDirectory(record.Id);
            if (Directory.Exists(directory))
            {
                stagedDeletion = Path.Combine(_rootDirectory, $".delete-{record.Id:N}");
                if (Directory.Exists(stagedDeletion))
                    Directory.Delete(stagedDeletion, recursive: true);
                Directory.Move(directory, stagedDeletion);
            }
            catalog.Maps.Remove(record);
            RemoveMapFromVariantGroups(catalog, record.Id);
            await WriteCatalogAsync(catalog);
            if (stagedDeletion is not null && Directory.Exists(stagedDeletion))
                Directory.Delete(stagedDeletion, recursive: true);
        }
        catch
        {
            if (stagedDeletion is not null && Directory.Exists(stagedDeletion))
            {
                var restoreDirectory = GetMapDirectory(id);
                if (!Directory.Exists(restoreDirectory))
                    Directory.Move(stagedDeletion, restoreDirectory);
            }
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    public string GetFloorOnePath(MapRecord record)
    {
        var firstFloor = MapFloorRules.GetOrderedFloors(record).FirstOrDefault();
        return firstFloor is not null && !string.IsNullOrWhiteSpace(firstFloor.ImageFileName)
            ? GetSafeMapFilePath(GetMapDirectory(record.Id), firstFloor.ImageFileName)
            : GetStoredFloorImagePath(record.Id, record.FloorOneFileName, "floor-1");
    }
}
