using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private async Task<MapCatalogDocument> ReadCatalogAsync()
    {
        Directory.CreateDirectory(_rootDirectory);
        if (!File.Exists(CatalogPath))
            return new MapCatalogDocument
            {
                StorageSchemaVersion = CurrentStorageSchemaVersion
            };

        MapCatalogDocument catalog;
        await using (var stream = File.OpenRead(CatalogPath))
        {
            catalog =
                await JsonSerializer.DeserializeAsync<MapCatalogDocument>(
                    stream,
                    SerializerOptions)
                ?? new MapCatalogDocument();
        }
        var migrated = false;
        var requiresLegacyBindingMigration = catalog.StorageSchemaVersion < 10;
        var needsV4Backup = false;
        var needsV5Backup = false;
        foreach (var record in catalog.Maps)
        {
            var previousSchema = record.Recognition?.SchemaVersion ?? 0;
            if (previousSchema < 5)
                needsV4Backup = true;
            if (previousSchema >= 5 && previousSchema < 6)
                needsV5Backup = true;
            var firstBoundsMissing =
                record.Recognition?.FirstFloor?.ValidMapBounds?.IsValid
                    is not true
                && record.Recognition?.FirstFloor?.RecognitionPixelWidth > 0
                && record.Recognition?.FirstFloor?.RecognitionPixelHeight > 0;
            var secondBoundsMissing =
                record.Recognition?.SecondFloor?.ValidMapBounds?.IsValid
                    is not true
                && record.Recognition?.SecondFloor?.RecognitionPixelWidth > 0
                && record.Recognition?.SecondFloor?.RecognitionPixelHeight > 0;
            record.NormalizeRecognition();

            migrated |= previousSchema < 6
                || firstBoundsMissing
                || secondBoundsMissing;

            if (requiresLegacyBindingMigration)
                await MigrateFloorImageBindingsAsync(record);
            else
                ValidateFloorBindingsFast(record);
        }
        migrated |= requiresLegacyBindingMigration
            || catalog.StorageSchemaVersion < CurrentStorageSchemaVersion;
        migrated |= NormalizeClasses(catalog);
        if (migrated && needsV4Backup)
        {
            var backupPath = $"{CatalogPath}.bak-v4";
            if (!File.Exists(backupPath))
                File.Copy(CatalogPath, backupPath);
        }
        if (migrated && needsV5Backup)
        {
            var backupPath = $"{CatalogPath}.bak-v5";
            if (!File.Exists(backupPath))
                File.Copy(CatalogPath, backupPath);
        }
        if (migrated)
        {
            catalog.StorageSchemaVersion = CurrentStorageSchemaVersion;
            await WriteCatalogAsync(catalog);
        }
        return catalog;
    }

    private async Task WriteCatalogAsync(MapCatalogDocument catalog)
    {
        catalog.StorageSchemaVersion = CurrentStorageSchemaVersion;
        var temporaryPath = $"{CatalogPath}.tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, catalog, SerializerOptions);
        File.Move(temporaryPath, CatalogPath, overwrite: true);
    }

    private static string? NormalizeClassName(string? name)
    {
        var normalized = name?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool NormalizeClasses(MapCatalogDocument catalog)
    {
        catalog.Classes ??= [];
        var canonical = new List<string>();
        foreach (var name in catalog.Classes.Concat(catalog.Maps.Select(map => map.Class)))
        {
            var normalized = NormalizeClassName(name) ?? "S1";
            if (!canonical.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                canonical.Add(normalized);
        }
        if (canonical.Count == 0)
            canonical.Add("S1");

        var changed = !catalog.Classes.SequenceEqual(canonical, StringComparer.Ordinal);
        catalog.Classes = canonical;
        foreach (var map in catalog.Maps)
        {
            var normalized = NormalizeClassName(map.Class) ?? "S1";
            var mapped = canonical.First(candidate => string.Equals(
                candidate, normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(map.Class, mapped, StringComparison.Ordinal))
            {
                map.Class = mapped;
                changed = true;
            }
        }
        return changed;
    }
}
