namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    /// <summary>Compatibility entry point. Reorders every Class using variant blocks.</summary>
    public async Task BatchRenameAllMapsToDefaultNamesAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            foreach (var className in catalog.Classes.OrderBy(name => name, StringComparer.Ordinal))
                ReorderClassCore(catalog, className);
            await WriteCatalogAsync(catalog);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RenameClassAsync(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Class 名称不能为空。");
        var normalizedNew = NormalizeClassName(newName)
            ?? throw new InvalidOperationException("无效的 Class 名称。");

        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var canonicalOld = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate, oldName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"找不到 Class '{oldName}'。");
            if (catalog.Classes.Any(candidate => string.Equals(
                    candidate, normalizedNew, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Class '{normalizedNew}' 已存在。");
            }

            catalog.Classes.Remove(canonicalOld);
            catalog.Classes.Add(normalizedNew);
            var properties = GetClassProperties(catalog, canonicalOld);
            catalog.ClassProperties.Remove(canonicalOld);
            catalog.ClassProperties[normalizedNew] = properties;
            foreach (var map in catalog.Maps.Where(map => string.Equals(
                         map.Class, canonicalOld, StringComparison.OrdinalIgnoreCase)))
                map.Class = normalizedNew;
            foreach (var group in catalog.VariantGroups.Where(group => string.Equals(
                         group.Class, canonicalOld, StringComparison.OrdinalIgnoreCase)))
                group.Class = normalizedNew;
            await WriteCatalogAsync(catalog);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<MapVariantGroupChangeResult> ToggleVariantGroupAsync(
        string className,
        IReadOnlyCollection<Guid> selectedMapIds)
    {
        ArgumentNullException.ThrowIfNull(selectedMapIds);
        var selected = selectedMapIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (selected.Length < 2)
            throw new InvalidOperationException("请至少选择两张地图来绑定或解绑变体。");

        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var canonicalClass = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate,
                className,
                StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("当前 Class 已不存在。");
            var selectedSet = selected.ToHashSet();
            var maps = catalog.Maps.Where(map => selectedSet.Contains(map.Id)).ToArray();
            if (maps.Length != selected.Length
                || maps.Any(map => !string.Equals(
                    map.Class,
                    canonicalClass,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("变体组合只能包含当前 Class 中仍然存在的地图。");
            }

            var intersecting = catalog.VariantGroups
                .Where(group => group.MapIds.Any(selectedSet.Contains))
                .ToArray();
            if (intersecting.Length == 1
                && intersecting[0].MapIds.Count == selectedSet.Count
                && intersecting[0].MapIds.All(selectedSet.Contains))
            {
                var removed = intersecting[0].Clone();
                catalog.VariantGroups.Remove(intersecting[0]);
                await WriteCatalogAsync(catalog);
                return new MapVariantGroupChangeResult(
                    MapVariantGroupChangeKind.Unbound,
                    removed);
            }
            if (intersecting.Length != 0)
            {
                throw new InvalidOperationException(
                    "选区包含已有变体组的部分成员或额外地图。解绑时必须且只能选择完整组合。");
            }

            var occupied = catalog.VariantGroups
                .Where(group => string.Equals(
                    group.Class,
                    canonicalClass,
                    StringComparison.OrdinalIgnoreCase))
                .Select(group => group.PaletteSlot)
                .ToHashSet();
            var paletteSlot = Enumerable.Range(0, MapVariantGroup.PaletteSize)
                .FirstOrDefault(slot => !occupied.Contains(slot), -1);
            if (paletteSlot < 0)
                throw new InvalidOperationException("当前 Class 已达到 12 个变体组合上限。");

            var catalogOrder = catalog.Maps
                .Select((map, index) => (map.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index);
            var group = new MapVariantGroup
            {
                Id = Guid.NewGuid(),
                Class = canonicalClass,
                PaletteSlot = paletteSlot,
                MapIds = maps
                    .OrderBy(map => map.SequenceNumber)
                    .ThenBy(map => catalogOrder[map.Id])
                    .Select(map => map.Id)
                    .ToList()
            };
            catalog.VariantGroups.Add(group);
            await WriteCatalogAsync(catalog);
            return new MapVariantGroupChangeResult(
                MapVariantGroupChangeKind.Bound,
                group.Clone());
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task ReorderClassAsync(string className)
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var canonicalClass = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate,
                className,
                StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("当前 Class 已不存在。");
            ReorderClassCore(catalog, canonicalClass);
            await WriteCatalogAsync(catalog);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void ReorderClassCore(MapCatalogDocument catalog, string className)
    {
        var originalIndex = catalog.Maps
            .Select((map, index) => (map.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        var orderedMaps = catalog.Maps
            .Where(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber)
            .ThenBy(map => originalIndex[map.Id])
            .ToArray();
        var mapsById = orderedMaps.ToDictionary(map => map.Id);
        var groups = catalog.VariantGroups
            .Where(group => string.Equals(group.Class, className, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var groupByMap = groups
            .SelectMany(group => group.MapIds.Select(mapId => (mapId, group)))
            .ToDictionary(pair => pair.mapId, pair => pair.group);
        var emittedGroups = new HashSet<Guid>();
        var result = new List<MapRecord>(orderedMaps.Length);
        foreach (var map in orderedMaps)
        {
            if (!groupByMap.TryGetValue(map.Id, out var group))
            {
                result.Add(map);
                continue;
            }
            if (!emittedGroups.Add(group.Id))
                continue;
            var members = group.MapIds
                .Select(mapId => mapsById[mapId])
                .OrderBy(member => member.SequenceNumber)
                .ThenBy(member => originalIndex[member.Id])
                .ToArray();
            group.MapIds = members.Select(member => member.Id).ToList();
            result.AddRange(members);
        }
        for (var index = 0; index < result.Count; index++)
        {
            result[index].Title = string.Empty;
            result[index].SequenceNumber = index + 1;
        }
    }

    private static void RemoveMapFromVariantGroups(
        MapCatalogDocument catalog,
        Guid mapId)
    {
        foreach (var group in catalog.VariantGroups)
            group.MapIds.Remove(mapId);
        catalog.VariantGroups.RemoveAll(group => group.MapIds.Count < 2);
    }
}
