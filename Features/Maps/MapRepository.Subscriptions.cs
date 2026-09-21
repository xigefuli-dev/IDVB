using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapSubscriptionImportedClass(string SourceName, string ImportedClassName);

public sealed record MapSubscriptionPromotionResult(
    IReadOnlyDictionary<string, string> ClassBindings,
    IReadOnlyList<Guid> InstalledMapIds);

public sealed partial class MapRepository
{
    private static readonly Guid ProcessInstanceId = Guid.NewGuid();

    public async Task MarkPublishedMapsAsSubscriptionAsync(
        IReadOnlyCollection<Guid> mapIds, Guid publicationId, string publisherDisplayName,
        string publisherKeyId, string version, bool isOfficialPublisher = false,
        bool isBuilderPublisher = false, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadCatalogAsync();
            var ids = mapIds.ToHashSet();
            foreach (var map in catalog.Maps.Where(map => ids.Contains(map.Id)))
            {
                map.AcquisitionKind = MapAcquisitionKind.Subscription;
                map.SubscriptionId = publicationId;
                map.SubscriptionPublisherHandle = publisherDisplayName;
                map.SubscriptionPublisherIsOfficial = isOfficialPublisher;
                map.SubscriptionPublisherIsBuilder = isBuilderPublisher;
                map.SubscriptionPublisherKeyId = publisherKeyId;
                map.SubscriptionVersion = version;
            }
            await WriteCatalogAsync(catalog);
        }
        finally { Gate.Release(); }
    }

    public async Task<MapSubscriptionPromotionResult> PromoteSubscriptionImportAsync(
        IReadOnlyList<MapSubscriptionImportedClass> importedClasses,
        IReadOnlyDictionary<string, string> previousBindings,
        IReadOnlyCollection<Guid> previousMapIds,
        Guid subscriptionId,
        string publisherDisplayName,
        string publisherKeyId,
        string version,
        bool isOfficialPublisher = false,
        bool isBuilderPublisher = false,
        CancellationToken cancellationToken = default)
    {
        if (importedClasses.Count == 0
            || importedClasses.Select(item => item.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != importedClasses.Count
            || importedClasses.Select(item => item.ImportedClassName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != importedClasses.Count)
            throw new InvalidOperationException("订阅导入的地图类映射无效。");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadCatalogAsync();
            foreach (var imported in importedClasses)
            {
                if (!catalog.Classes.Contains(imported.ImportedClassName, StringComparer.OrdinalIgnoreCase)
                    || !catalog.Maps.Any(map => string.Equals(
                        map.Class, imported.ImportedClassName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("订阅导入的临时地图类已不存在。");
            }

            var oldMapIds = previousMapIds.ToHashSet();
            var newMapIds = catalog.Maps
                .Where(map => importedClasses.Any(item => string.Equals(
                    item.ImportedClassName, map.Class, StringComparison.OrdinalIgnoreCase)))
                .Select(map => map.Id)
                .ToHashSet();
            var oldMaps = catalog.Maps.Where(map => oldMapIds.Contains(map.Id)).ToArray();
            var newMaps = catalog.Maps.Where(map => newMapIds.Contains(map.Id)).ToArray();
            if (oldMaps.Length != oldMapIds.Count
                || oldMaps.Any(map => !previousBindings.Values.Contains(
                    map.Class, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("原订阅地图或地图类映射不完整，无法安全替换。");
            foreach (var imported in importedClasses)
            {
                var replacementMaps = newMaps.Where(map => string.Equals(
                    map.Class, imported.ImportedClassName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var previousMaps = previousBindings.TryGetValue(imported.SourceName, out var previousClass)
                    ? oldMaps.Where(map => string.Equals(
                        map.Class, previousClass, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : [];
                var previouslyScannableFloors = previousMaps
                    .SelectMany(map => MapFloorRules.GetOrderedFloors(map)
                        .Where(floor => MapScanFloorRules.HasRequiredScanMarkers(map, floor.Key))
                        .Select(floor => MapScanFloorRules.NormalizeFloorIdentity(floor.Key)!))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var map in replacementMaps)
                {
                    var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
                    foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                    {
                        if (!HasPrebuiltStructureLine(map, floor.Key))
                            throw new InvalidOperationException(
                                $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 的预制结构图不完整。");
                        var prebuilt = floor.PrebuiltStructureLine!;
                        var algorithmPath = GetPrebuiltStructureAlgorithmPath(map, floor.Key);
                        if (!File.Exists(algorithmPath))
                            throw new InvalidOperationException(
                                $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 缺少预制结构算法。");
                        if (!string.Equals(ComputeFileSha256(GetPrebuiltStructureLinePath(map, floor.Key)),
                                prebuilt.Sha256, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(ComputeFileSha256(algorithmPath),
                                prebuilt.AlgorithmSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 的预制结构图哈希不匹配。");

                        if (!string.Equals(floor.Key, scanFloorKey,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!MapScanFloorRules.HasRequiredScanMarkers(map, floor.Key))
                            throw new InvalidOperationException(
                                $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 缺少扫描门锚点。");
                        if (!TryGetValidSideEntranceFeaturePath(map, floor.Key, out _, out var reason))
                            throw new InvalidOperationException(
                                $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 的侧门特征无效：{reason}");
                    }
                    foreach (var floorIdentity in previouslyScannableFloors)
                        if (MapScanFloorRules.ResolveFloorKey(map, floorIdentity) is null)
                            throw new InvalidOperationException(
                                $"订阅地图 {map.DisplayName} 缺少原有可扫描楼层 {floorIdentity}。");
                }
            }
            // The runtime cache reads each feature as grayscale. Verify that
            // final read here before replacing the old subscription maps.
            foreach (var map in newMaps)
            {
                var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
                foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                {
                    if (!string.Equals(floor.Key, scanFloorKey,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TryGetValidSideEntranceFeaturePath(map, floor.Key, out var path, out var reason))
                        throw new InvalidOperationException(
                            $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 无法进入侧门缓存：{reason}");
                    using var feature = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Grayscale);
                    if (feature.Empty())
                        throw new InvalidOperationException(
                            $"订阅地图 {map.DisplayName} 楼层 {floor.Key} 的侧门特征无法解码。");
                }
            }
            oldMapIds.ExceptWith(newMapIds);
            catalog.Maps.RemoveAll(map => oldMapIds.Contains(map.Id));
            catalog.VariantGroups.RemoveAll(group => group.MapIds.Any(oldMapIds.Contains));
            foreach (var map in catalog.Maps.Where(map => newMapIds.Contains(map.Id)))
            {
                map.AcquisitionKind = MapAcquisitionKind.Subscription;
                map.SubscriptionId = subscriptionId;
                map.SubscriptionPublisherHandle = publisherDisplayName;
                map.SubscriptionPublisherIsOfficial = isOfficialPublisher;
                map.SubscriptionPublisherIsBuilder = isBuilderPublisher;
                map.SubscriptionPublisherKeyId = publisherKeyId;
                map.SubscriptionVersion = version;
            }

            var nextBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imported in importedClasses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var importedCanonical = catalog.Classes.Single(name => string.Equals(
                    name, imported.ImportedClassName, StringComparison.OrdinalIgnoreCase));
                var desiredClass = previousBindings.TryGetValue(imported.SourceName, out var previousClass)
                    ? catalog.Classes.FirstOrDefault(name => string.Equals(
                        name, previousClass, StringComparison.OrdinalIgnoreCase))
                    : null;
                desiredClass ??= importedCanonical;
                nextBindings[imported.SourceName] = desiredClass;
                if (string.Equals(desiredClass, importedCanonical, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var map in catalog.Maps.Where(map => string.Equals(
                    map.Class, importedCanonical, StringComparison.OrdinalIgnoreCase)))
                    map.Class = desiredClass;
                foreach (var group in catalog.VariantGroups.Where(group => string.Equals(
                    group.Class, importedCanonical, StringComparison.OrdinalIgnoreCase)))
                    group.Class = desiredClass;
                if (catalog.ClassProperties.TryGetValue(importedCanonical, out var importedProperties))
                {
                    var merged = importedProperties.Clone();
                    if (catalog.ClassProperties.TryGetValue(desiredClass, out var localProperties))
                    {
                        merged.ImageDownsampleFactor = localProperties.ImageDownsampleFactor;
                        merged.BackgroundRemovalIntensity = localProperties.BackgroundRemovalIntensity;
                    }
                    catalog.ClassProperties[desiredClass] = merged;
                }
                catalog.ClassProperties.Remove(importedCanonical);
                catalog.Classes.Remove(importedCanonical);
            }

            foreach (var removedBinding in previousBindings
                .Where(pair => !nextBindings.ContainsKey(pair.Key)))
            {
                var className = catalog.Classes.FirstOrDefault(name => string.Equals(
                    name, removedBinding.Value, StringComparison.OrdinalIgnoreCase));
                if (className is null
                    || catalog.Maps.Any(map => string.Equals(
                        map.Class, className, StringComparison.OrdinalIgnoreCase))
                    || catalog.Classes.Count <= 1)
                    continue;
                catalog.Classes.Remove(className);
                catalog.ClassProperties.Remove(className);
            }

            await WriteCatalogAsync(catalog);
            try { WriteSubscriptionRetirementJournal(oldMapIds); }
            catch { }
            return new MapSubscriptionPromotionResult(
                nextBindings,
                newMapIds.Order().ToArray());
        }
        finally
        {
            Gate.Release();
        }
    }

    private void WriteSubscriptionRetirementJournal(IReadOnlyCollection<Guid> mapIds)
    {
        if (mapIds.Count == 0) return;
        var journal = new SubscriptionRetirementJournal
        {
            ProcessInstanceId = ProcessInstanceId,
            MapIds = mapIds.ToList()
        };
        var path = Path.Combine(_rootDirectory, $".subscription-retired-{Guid.NewGuid():N}.json");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions));
        File.Move(temporary, path);
    }

    private void RecoverRetiredSubscriptionMaps()
    {
        if (!Directory.Exists(_rootDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(
            _rootDirectory, ".subscription-retired-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<SubscriptionRetirementJournal>(
                    File.ReadAllBytes(path), SerializerOptions);
                if (journal is null || journal.ProcessInstanceId == ProcessInstanceId) continue;
                foreach (var mapId in journal.MapIds)
                {
                    var directory = GetMapDirectory(mapId);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
                File.Delete(path);
            }
            catch
            {
                // Keep evidence for a later safe retry when a decoder still owns a retired file.
            }
        }
    }

    private sealed class SubscriptionRetirementJournal
    {
        public Guid ProcessInstanceId { get; set; }
        public List<Guid> MapIds { get; set; } = [];
    }
}
