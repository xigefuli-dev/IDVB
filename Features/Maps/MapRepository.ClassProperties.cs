namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    /// <summary>
    /// Rebuilds every map in a Class before committing the Class property.
    /// The returned IDs are the maps whose derived resources were rebuilt.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SetClassRemoveBackgroundAsync(
        string className,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var canonicalName = NormalizeClassName(className)
            ?? throw new InvalidOperationException("Class 名称不能为空。");
        // Keep every repository writer out until this multi-map operation has
        // either committed or restored its snapshot. In particular, rollback
        // must never write an old catalog over a successful concurrent save.
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadCatalogAsync();
            var actualName = catalog.Classes.SingleOrDefault(candidate =>
                string.Equals(candidate, canonicalName, StringComparison.OrdinalIgnoreCase));
            if (actualName is null)
                throw new InvalidOperationException("找不到要修改的 Class。");
            canonicalName = actualName;
            var currentProperties = GetClassProperties(catalog, canonicalName);
            if (currentProperties.RemoveBackground == enabled)
                return Array.Empty<Guid>();
            var maps = catalog.Maps
                .Where(map => string.Equals(map.Class, canonicalName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(map => map.SequenceNumber)
                .Select(map => map.Clone())
                .ToList();
            var originalCatalog = await File.ReadAllBytesAsync(CatalogPath, cancellationToken);
            var operationDirectory = Path.Combine(_rootDirectory, $".class-property-{Guid.NewGuid():N}");
            var backupDirectory = Path.Combine(operationDirectory, "maps");
            Directory.CreateDirectory(backupDirectory);
            try
            {
                foreach (var map in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceDirectory = GetMapDirectory(map.Id);
                    if (!Directory.Exists(sourceDirectory))
                        throw new InvalidOperationException($"地图 {map.DisplayName} 的资源目录不存在。");
                    CopyDirectory(sourceDirectory, Path.Combine(backupDirectory, map.Id.ToString("N")));
                }

                foreach (var map in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var draft = await CreateDraftCoreAsync(map.Id, gateAlreadyHeld: true)
                        ?? throw new InvalidOperationException($"地图 {map.DisplayName} 已不存在。");
                    draft.RemoveBackgroundOverride = enabled;
                    await SaveCoreAsync(
                        draft,
                        gateAlreadyHeld: true);
                }

                var updatedCatalog = await ReadCatalogAsync();
                var properties = GetClassProperties(updatedCatalog, canonicalName);
                properties.RemoveBackground = enabled;
                updatedCatalog.ClassProperties[canonicalName] = properties;
                await WriteCatalogAsync(updatedCatalog);

                return maps.Select(map => map.Id).ToArray();
            }
            catch
            {
                // Restore both the local files and catalog bytes. This also rolls
                // back ContentVersion/UpdatedAt changes made by individual saves.
                foreach (var map in maps)
                {
                    var backup = Path.Combine(backupDirectory, map.Id.ToString("N"));
                    var target = GetMapDirectory(map.Id);
                    if (!Directory.Exists(backup))
                        continue;
                    if (Directory.Exists(target))
                        Directory.Delete(target, recursive: true);
                    CopyDirectory(backup, target);
                }
                await File.WriteAllBytesAsync(CatalogPath, originalCatalog, CancellationToken.None);
                throw;
            }
            finally
            {
                if (Directory.Exists(operationDirectory))
                    Directory.Delete(operationDirectory, recursive: true);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
