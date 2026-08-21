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
        if (catalog.StorageSchemaVersion > CurrentStorageSchemaVersion)
            throw new InvalidDataException("地图目录由更高版本的 Identity Vision Bridge 创建，当前版本不能安全写入。");
        var originalStorageSchemaVersion = catalog.StorageSchemaVersion;
        if (originalStorageSchemaVersion < 16)
            await EnsureVariantMigrationBackupAsync();
        var migrated = false;
        var repairedFloorProfileIds = new List<Guid>();
        var requiresLegacyBindingMigration = catalog.StorageSchemaVersion < 10;
        var requiresFloorProfileMigration = catalog.StorageSchemaVersion < CurrentStorageSchemaVersion
            || catalog.Maps.Any(record => record.NeedsCanonicalFloorNormalization());
        if (requiresFloorProfileMigration)
        {
            var backupPath = $"{CatalogPath}.bak-v14";
            if (!File.Exists(backupPath))
                File.Copy(CatalogPath, backupPath);
        }
        var needsV4Backup = false;
        var needsV5Backup = false;
        foreach (var record in catalog.Maps)
        {
            var needsFloorProfileRepair = record.NeedsCanonicalFloorNormalization();
            if (needsFloorProfileRepair)
                repairedFloorProfileIds.Add(record.Id);
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
                || secondBoundsMissing
                || needsFloorProfileRepair;

            if (requiresLegacyBindingMigration)
                await MigrateFloorImageBindingsAsync(record);
            else
                ValidateFloorBindingsFast(record);
        }
        migrated |= requiresLegacyBindingMigration
            || catalog.StorageSchemaVersion < CurrentStorageSchemaVersion;
        migrated |= NormalizeClasses(catalog);
        if (originalStorageSchemaVersion < 16)
        {
            catalog.VariantGroups = [];
            migrated = true;
        }
        else
        {
            migrated |= ValidateVariantGroups(catalog, normalizeClassCasing: true);
        }
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
        foreach (var mapId in repairedFloorProfileIds)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MapRepository] repaired canonical floor profiles for map {mapId}");
        }
        return catalog;
    }

    private async Task WriteCatalogAsync(MapCatalogDocument catalog)
    {
        ValidateVariantGroups(catalog, normalizeClassCasing: false);
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
        catalog.ClassProperties ??= new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
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

        var properties = new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
        foreach (var className in canonical)
        {
            var existing = catalog.ClassProperties.FirstOrDefault(pair =>
                string.Equals(pair.Key, className, StringComparison.OrdinalIgnoreCase)).Value;
            properties[className] = existing?.Clone() ?? new MapClassProperties();
        }
        if (catalog.ClassProperties.Count != properties.Count
            || catalog.ClassProperties.Any(pair => !properties.ContainsKey(pair.Key)
                || !properties[pair.Key].Equals(pair.Value)))
        {
            changed = true;
        }
        catalog.ClassProperties = properties;
        return changed;
    }

    private static MapClassProperties GetClassProperties(
        MapCatalogDocument catalog,
        string? className)
    {
        if (!string.IsNullOrWhiteSpace(className)
            && catalog.ClassProperties.TryGetValue(className, out var properties))
        {
            return properties.Clone();
        }
        return new MapClassProperties();
    }

    private static bool ValidateVariantGroups(
        MapCatalogDocument catalog,
        bool normalizeClassCasing)
    {
        catalog.VariantGroups ??= [];
        var changed = false;
        var groupIds = new HashSet<Guid>();
        var memberIds = new HashSet<Guid>();
        var paletteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapsById = catalog.Maps.ToDictionary(map => map.Id);
        foreach (var group in catalog.VariantGroups)
        {
            if (group is null || group.Id == Guid.Empty || !groupIds.Add(group.Id))
                throw new InvalidDataException("地图目录包含无效或重复的变体组 ID。");
            var canonicalClass = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate,
                group.Class,
                StringComparison.OrdinalIgnoreCase));
            if (canonicalClass is null)
                throw new InvalidDataException($"变体组 {group.Id} 引用了不存在的 Class。");
            if (!string.Equals(group.Class, canonicalClass, StringComparison.Ordinal))
            {
                if (!normalizeClassCasing)
                    throw new InvalidDataException($"变体组 {group.Id} 的 Class 名称不是规范值。");
                group.Class = canonicalClass;
                changed = true;
            }
            if (group.PaletteSlot is < 0 or >= MapVariantGroup.PaletteSize)
                throw new InvalidDataException($"变体组 {group.Id} 的颜色槽无效。");
            if (!paletteKeys.Add($"{canonicalClass}\0{group.PaletteSlot}"))
                throw new InvalidDataException($"Class “{canonicalClass}”存在重复的变体颜色槽。");
            group.MapIds ??= [];
            if (group.MapIds.Count < 2 || group.MapIds.Count != group.MapIds.Distinct().Count())
                throw new InvalidDataException($"变体组 {group.Id} 必须包含至少两张互不重复的地图。");
            foreach (var mapId in group.MapIds)
            {
                if (!mapsById.TryGetValue(mapId, out var map)
                    || !string.Equals(map.Class, canonicalClass, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"变体组 {group.Id} 包含不存在或跨 Class 的地图。");
                }
                if (!memberIds.Add(mapId))
                    throw new InvalidDataException($"地图 {mapId} 同时属于多个变体组。");
            }
        }
        if (catalog.VariantGroups.GroupBy(group => group.Class, StringComparer.OrdinalIgnoreCase)
            .Any(groups => groups.Count() > MapVariantGroup.PaletteSize))
        {
            throw new InvalidDataException("单个 Class 的变体组数量超过 12。");
        }
        return changed;
    }
}
/*
 * 文件职责：MapRepository.Catalog。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
