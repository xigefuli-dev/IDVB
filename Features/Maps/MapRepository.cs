using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public readonly record struct MapCatalogRevision(long LastWriteUtcTicks, long Length)
{
    public static MapCatalogRevision Empty { get; } = new(0, 0);
}

/// <summary>
/// Persists map metadata and its two source images in the application's local data directory.
/// </summary>
public sealed partial class MapRepository
{
    private const int CurrentStorageSchemaVersion = 16;
    private const string FloorOneRecognitionFileName = "floor-1-recognition.png";
    private const string FloorTwoRecognitionFileName = "floor-2-recognition.png";
    private const string FloorOneOverlayFileName = "floor-1-overlay.png";
    private const string FloorTwoOverlayFileName = "floor-2-overlay.png";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;
    private readonly SideEntranceFeaturePreprocessor _sideEntrancePreprocessor = new();

    public MapRepository(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "Maps");
        RecoverInterruptedIdvmImports();
    }

    private string CatalogPath => Path.Combine(_rootDirectory, "maps.json");

    public async Task<IReadOnlyList<MapRecord>> GetMapsAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            return catalog.Maps
                .OrderBy(record => record.SequenceNumber)
                .Select(record => record.Clone())
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<MapDraft?> CreateDraftAsync(Guid id)
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var record = catalog.Maps.SingleOrDefault(map => map.Id == id);
            if (record is null)
                return null;
            // The list path uses file stamps to stay responsive. Editing a
            // specific map is the point where we perform the full content
            // verification before exposing its assets to the editor.
            await VerifyFloorImageBindingsAsync(record);

            var floorPaths = record.Floors.ToDictionary(
                floor => floor.Key,
                floor => GetEditableFloorImagePath(record, floor.Key),
                StringComparer.Ordinal);
            var floorPreviewPaths = floorPaths.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    var previewPath = GetFloorRecognitionPath(record, pair.Key);
                    return File.Exists(previewPath) ? previewPath : pair.Value;
                },
                StringComparer.Ordinal);

            return new MapDraft
            {
                Id = record.Id,
                FloorOnePath = GetFloorOnePath(record),
                FloorTwoPath = GetFloorTwoPath(record),
                FloorPaths = floorPaths,
                FloorPreviewPaths = floorPreviewPaths,
                FloorRecognitionSourcePaths = string.Equals(record.Source, "survey", StringComparison.Ordinal)
                    ? record.Floors.ToDictionary(
                        floor => floor.Key,
                        floor => GetFloorRecognitionPath(record, floor.Key),
                        StringComparer.Ordinal)
                    : [],
                Floors = record.Floors.Select(f => new FloorDefinition
                {
                    Key = f.Key,
                    DisplayName = f.DisplayName,
                    SortOrder = f.SortOrder
                }).ToList(),
                Class = record.Class,
                ClassProperties = GetClassProperties(catalog, record.Class),
                Title = record.Title,
                ContentVersion = record.ContentVersion,
                Source = record.Source,
                SourceProjectId = record.SourceProjectId,
                SourceProjectRevision = record.SourceProjectRevision,
                SourceVisualSha256 = record.SourceVisualSha256,
                SourceStructureSha256 = record.SourceStructureSha256,
                PortableGates = record.PortableGates.Select(gate => gate.Clone()).ToList(),
                Recognition = record.Recognition.Clone()
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<MapRecord> SaveAsync(MapDraft draft,
        int sideEntranceFeatureRadius = MapRecognitionTuning.DefaultSideEntranceFeatureRadius) =>
        Task.Run(() => SaveCoreAsync(draft, sideEntranceFeatureRadius));

    private async Task<MapRecord> SaveCoreAsync(MapDraft draft,
        int sideEntranceFeatureRadius = MapRecognitionTuning.DefaultSideEntranceFeatureRadius)
    {
        ValidateDraft(draft);

        await Gate.WaitAsync();
        string? stagingDirectory = null;
        string? backupDirectory = null;
        string? targetDirectory = null;
        var isNewRecord = false;
        try
        {
            var catalog = await ReadCatalogAsync();
            var existing = draft.Id is { } id ? catalog.Maps.SingleOrDefault(map => map.Id == id) : null;
            if (draft.Id is not null && existing is null && !draft.CreateAsImportedCopy)
                throw new InvalidOperationException("找不到要编辑的地图。");

            var record = existing ?? new MapRecord
            {
                Id = draft.Id ?? Guid.NewGuid(),
                SequenceNumber = catalog.NextSequenceNumber++,
                CreatedAt = DateTimeOffset.UtcNow
            };
            isNewRecord = existing is null;
            record.Title = draft.Title?.Trim() ?? string.Empty;
            record.Source = string.Equals(draft.Source, "survey", StringComparison.Ordinal)
                ? "survey"
                : "manual";
            record.SourceProjectId = draft.SourceProjectId;
            record.SourceProjectRevision = draft.SourceProjectRevision;
            record.SourceVisualSha256 = draft.SourceVisualSha256;
            record.SourceStructureSha256 = draft.SourceStructureSha256;
            record.ContentVersion = existing is null
                ? Math.Max(1, draft.ContentVersion)
                : Math.Max(1, existing.ContentVersion + 1);
            record.PortableGates = draft.PortableGates
                .Select(gate => gate.Clone())
                .ToList();
            record.UpdatedAt = DateTimeOffset.UtcNow;

            // A draft can only be saved into an existing canonical class.
            var requestedClass = NormalizeClassName(draft.Class) ?? "S1";
            var targetClass = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate, requestedClass, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("所选 Class 已不存在，请返回列表后重试。");
            if (existing is not null
                && !string.Equals(existing.Class, targetClass, StringComparison.OrdinalIgnoreCase)
                && catalog.VariantGroups.Any(group => group.MapIds.Contains(existing.Id)))
            {
                throw new InvalidOperationException("已绑定变体的地图不能移动到其他 Class，请先解绑变体组合。");
            }
            record.Class = targetClass;
            if (draft.Floors.Count > 0)
                record.Floors = draft.Floors
                    .OrderBy(floor => floor.SortOrder)
                    .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                    .Select((floor, index) => new FloorDefinition
                    {
                        Key = floor.Key,
                        DisplayName = floor.DisplayName,
                        SortOrder = index + 1
                    })
                    .ToList();
            record.Recognition = draft.Recognition.Clone();
            record.NormalizeRecognition();
            ValidateFloorDefinitions(record);

            Directory.CreateDirectory(_rootDirectory);
            stagingDirectory = Path.Combine(_rootDirectory, $".pending-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);

            // Copy each floor's original image by its explicit floor key.
            var floorImageFileNames = new Dictionary<string, string>();
            foreach (var (key, path) in draft.FloorPaths
                .Where(kvp => IsSupportedImage(kvp.Value) && File.Exists(kvp.Value))
                .OrderBy(kvp => draft.Floors.FirstOrDefault(f => f.Key == kvp.Key)?.SortOrder ?? 99)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                var filePrefix = GetFloorImageFilePrefix(key);
                var copiedName = await CopyImageToDirectoryAsync(path, stagingDirectory, filePrefix);
                floorImageFileNames[key] = copiedName;
            }
            var orderedFloorKeys = record.Floors
                .OrderBy(floor => floor.SortOrder)
                .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                .Select(floor => floor.Key)
                .ToArray();
            // Legacy draft fields are positional fallbacks only. Resolve them
            // to the actual ordered floor keys before persisting any file.
            for (var index = 0; index < orderedFloorKeys.Length && index < 2; index++)
            {
                var key = orderedFloorKeys[index];
                if (floorImageFileNames.ContainsKey(key))
                    continue;
                var legacyPath = index == 0 ? draft.FloorOnePath : draft.FloorTwoPath;
                if (IsSupportedImage(legacyPath) && File.Exists(legacyPath!))
                    floorImageFileNames[key] = await CopyImageToDirectoryAsync(
                        legacyPath!,
                        stagingDirectory,
                        GetFloorImageFilePrefix(key));
            }
            // Legacy readers still resolve the first two floors through these
            // fields, so keep them aligned with the reordered floor list even
            // when the current IDs are custom names.
            if (orderedFloorKeys.Length > 0
                && floorImageFileNames.TryGetValue(orderedFloorKeys[0], out var firstFloorFileName))
            {
                record.FloorOneFileName = firstFloorFileName;
            }
            if (orderedFloorKeys.Length > 1
                && floorImageFileNames.TryGetValue(orderedFloorKeys[1], out var secondFloorFileName))
            {
                record.FloorTwoFileName = secondFloorFileName;
            }

            await PopulateFloorImageMetadataAsync(
                record,
                stagingDirectory,
                floorImageFileNames);

            // Generate and explicitly bind both derived assets for each floor.
            foreach (var (key, fileName) in floorImageFileNames)
            {
                var sourcePath = Path.Combine(stagingDirectory, fileName);
                if (!File.Exists(sourcePath)) continue;
                var floor = record.Floors.Single(candidate => candidate.Key == key);
                var profile = record.Recognition.GetFloor(key)!;
                var recognitionFileName = GetFloorRecognitionFileName(key);
                var overlayFileName = GetFloorOverlayFileName(key);
                var recognitionPath = Path.Combine(stagingDirectory, recognitionFileName);
                var overlayPath = Path.Combine(stagingDirectory, overlayFileName);
                var hasSurveyStructure = draft.FloorRecognitionSourcePaths.TryGetValue(
                    key,
                    out var surveyStructurePath)
                    && IsSupportedImage(surveyStructurePath)
                    && File.Exists(surveyStructurePath);
                var removeBackground = draft.RemoveBackgroundOverride
                    ?? GetClassProperties(catalog, record.Class).RemoveBackground;
                var needsIndependentRecognition = removeBackground
                    || profile.BackgroundLayers.Count > 0
                    || !UsesWholeSourceImage(profile);
                if (hasSurveyStructure)
                {
                    using var surveyImage = Cv2.ImRead(surveyStructurePath!, ImreadModes.Unchanged);
                    if (surveyImage.Empty())
                        throw new InvalidOperationException("测绘识别结构无法解码。");
                    using var processed = MapBackgroundProcessor.Process(
                        surveyImage,
                        profile,
                        removeBackground);
                    if (!needsIndependentRecognition && UsesWholeSourceImage(profile))
                        await CopyRecognitionSourceAsync(surveyStructurePath!, recognitionPath);
                    else if (!Cv2.ImWrite(recognitionPath, processed.Recognition))
                        throw new InvalidOperationException("无法保存测绘识别图。");
                    if (!Cv2.ImWrite(overlayPath, processed.Overlay))
                        throw new InvalidOperationException("无法保存测绘透明叠加图。");
                    profile.RecognitionPixelWidth = processed.Recognition.Width;
                    profile.RecognitionPixelHeight = processed.Recognition.Height;
                    profile.ValidMapBounds ??= MapReferenceBounds.FullImage(
                        processed.Recognition.Width,
                        processed.Recognition.Height);
                }
                else
                {
                    CreateRecognitionAssets(
                        sourcePath,
                        recognitionPath,
                        profile,
                        overlayPath,
                        removeBackground);
                }
                await PopulateDerivedImageMetadataAsync(
                    floor,
                    sourcePath,
                    recognitionPath,
                    overlayPath,
                    profile,
                    forceRecognitionPath: hasSurveyStructure || needsIndependentRecognition);

                // 侧门特征预处理：若侧门锚点已标注，生成特征图
                // IDVM 导入时优先复制包内预计算特征；普通编辑时重新生成
                if (draft.SideEntranceFeaturePaths.TryGetValue(key, out var importedFeaturePath)
                    && File.Exists(importedFeaturePath)
                    && profile.SideEntranceFeatureRadius > 0)
                {
                    await CopySideEntranceFeatureAsync(
                        importedFeaturePath, stagingDirectory, profile);
                }
                else
                {
                    await TryGenerateSideEntranceFeatureAsync(
                        stagingDirectory, profile, sideEntranceFeatureRadius);
                }

                var recognitionSourcePath = needsIndependentRecognition || !UsesWholeSourceImage(profile)
                    ? Path.Combine(stagingDirectory, recognitionFileName)
                    : sourcePath;
                var thumbnailPath = Path.Combine(
                    stagingDirectory,
                    GetFloorThumbnailFileName(key));
                await CreateThumbnailAsync(recognitionSourcePath, thumbnailPath);
                await PopulateThumbnailMetadataAsync(floor, thumbnailPath);

                // Keep the legacy first/second asset names aligned with the
                // ordered floor list, even when those floors use custom IDs
                // or the literal 1f/2f IDs have been reordered.
                var orderedPosition = Array.IndexOf(orderedFloorKeys, key);
                var compatibilityRecognitionFileName = orderedPosition switch
                {
                    0 => FloorOneRecognitionFileName,
                    1 => FloorTwoRecognitionFileName,
                    _ => null
                };
                var compatibilityOverlayFileName = orderedPosition switch
                {
                    0 => FloorOneOverlayFileName,
                    1 => FloorTwoOverlayFileName,
                    _ => null
                };
                if (compatibilityRecognitionFileName is not null
                    && compatibilityOverlayFileName is not null
                    && (!string.Equals(recognitionFileName, compatibilityRecognitionFileName, StringComparison.Ordinal)
                        || !string.Equals(overlayFileName, compatibilityOverlayFileName, StringComparison.Ordinal)))
                {
                    var compatibilityRecognitionPath = Path.Combine(
                        stagingDirectory,
                        compatibilityRecognitionFileName);
                    var compatibilityOverlayPath = Path.Combine(
                        stagingDirectory,
                        compatibilityOverlayFileName);
                    if (hasSurveyStructure)
                    {
                        await CopyRecognitionSourceAsync(
                            recognitionPath,
                            compatibilityRecognitionPath);
                        using var compatibilityImage = Cv2.ImRead(
                            compatibilityRecognitionPath,
                            ImreadModes.Unchanged);
                        CreateWhiteKeyOverlay(compatibilityImage, compatibilityOverlayPath);
                    }
                    else
                    {
                        CreateRecognitionAssets(
                            sourcePath,
                            compatibilityRecognitionPath,
                            profile,
                            compatibilityOverlayPath,
                            removeBackground);
                    }
                }
            }
            targetDirectory = GetMapDirectory(record.Id);
            if (Directory.Exists(targetDirectory))
            {
                backupDirectory = Path.Combine(_rootDirectory, $".backup-{record.Id:N}");
                if (Directory.Exists(backupDirectory))
                    Directory.Delete(backupDirectory, recursive: true);
                Directory.Move(targetDirectory, backupDirectory);
            }

            Directory.Move(stagingDirectory, targetDirectory);
            stagingDirectory = null;

            if (existing is null)
                catalog.Maps.Add(record);

            await WriteCatalogAsync(catalog);

            if (backupDirectory is not null && Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, recursive: true);

            return record.Clone();
        }
        catch
        {
            if (targetDirectory is not null && backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                if (Directory.Exists(targetDirectory))
                    Directory.Delete(targetDirectory, recursive: true);
                Directory.Move(backupDirectory, targetDirectory);
            }
            else if (isNewRecord && targetDirectory is not null && Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
            throw;
        }
        finally
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            Gate.Release();
        }
    }

    private static async Task CopyRecognitionSourceAsync(string source, string destination)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output);
        await output.FlushAsync();
    }
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
    public string GetFloorTwoPath(MapRecord record)
    {
        var secondFloor = MapFloorRules.GetOrderedFloors(record).Skip(1).FirstOrDefault();
        return secondFloor is not null && !string.IsNullOrWhiteSpace(secondFloor.ImageFileName)
            ? GetSafeMapFilePath(GetMapDirectory(record.Id), secondFloor.ImageFileName)
            : GetStoredFloorImagePath(record.Id, record.FloorTwoFileName, "floor-2");
    }
    public string GetFloorOneRecognitionPath(MapRecord record) =>
        GetFloorRecognitionPath(
            record,
            MapFloorRules.GetOrderedFloors(record).FirstOrDefault()?.Key
                 ?? record.Recognition.FirstFloor.FloorKey);
    public string GetFloorTwoRecognitionPath(MapRecord record) =>
        GetFloorRecognitionPath(
            record,
            MapFloorRules.GetOrderedFloors(record).Skip(1).FirstOrDefault()?.Key
                 ?? record.Recognition.SecondFloor.FloorKey);
    public string GetFloorOneOverlayPath(MapRecord record) =>
        GetFloorOverlayPath(
            record,
            MapFloorRules.GetOrderedFloors(record).FirstOrDefault()?.Key
                 ?? record.Recognition.FirstFloor.FloorKey);
    public string GetFloorTwoOverlayPath(MapRecord record) =>
        GetFloorOverlayPath(
            record,
            MapFloorRules.GetOrderedFloors(record).Skip(1).FirstOrDefault()?.Key
                ?? record.Recognition.SecondFloor.FloorKey);

    // ── V6: floor-key-based path helpers ──────────────────────────
    public string GetFloorImagePath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(f => f.Key == floorKey);
        if (floor is null)
            throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        EnsureSafeFloorKey(floorKey);
        // Explicit floor metadata is authoritative. The positional fallback is
        // only for pre-V9 records during migration/repair.
        return GetOrderedFloorPosition(record, floorKey) switch
        {
            0 => GetFloorOnePath(record),
            1 => GetFloorTwoPath(record),
            _ => GetEditableFloorImagePath(record, floorKey)
        };
    }

    /// <summary>Loads the complete management snapshot in one catalog read.</summary>
    public async Task<MapCatalogSnapshot> GetCatalogSnapshotAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            return new MapCatalogSnapshot(
                catalog.Classes.ToArray(),
                catalog.Maps.OrderBy(record => record.SequenceNumber)
                    .Select(record => record.Clone()).ToArray())
            {
                ClassProperties = catalog.ClassProperties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase),
                VariantGroups = catalog.VariantGroups
                    .Select(group => group.Clone())
                    .ToArray()
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task VerifyMapContentAsync(Guid id)
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var record = catalog.Maps.SingleOrDefault(map => map.Id == id)
                ?? throw new InvalidOperationException($"Map {id} was not found.");
            await VerifyFloorImageBindingsAsync(record);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<string> CreateClassAsync(string name)
    {
        var requestedName = NormalizeClassName(name)
            ?? throw new InvalidOperationException("Class 名称不能为空。");
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            if (catalog.Classes.Any(candidate => string.Equals(
                    candidate, requestedName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名 Class。");
            catalog.Classes.Add(requestedName);
            catalog.ClassProperties[requestedName] = new MapClassProperties();
            await WriteCatalogAsync(catalog);
            return requestedName;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Creates a new class without ever merging with an existing name.</summary>
    public Task<string> CreateUniqueClassAsync(string sourceName) =>
        CreateUniqueClassAsync(sourceName, beforeCommit: null);

    private async Task<string> CreateUniqueClassAsync(
        string sourceName,
        Action<string>? beforeCommit)
    {
        var requestedName = NormalizeClassName(sourceName)
            ?? throw new InvalidOperationException("Class 名称不能为空。");
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var uniqueName = BuildUniqueImportedClassName(requestedName, catalog.Classes);
            beforeCommit?.Invoke(uniqueName);
            catalog.Classes.Add(uniqueName);
            catalog.ClassProperties[uniqueName] = new MapClassProperties();
            await WriteCatalogAsync(catalog);
            return uniqueName;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Removes a class and all maps assigned to it as one catalog operation.</summary>
    public async Task<MapClassDeletionResult> DeleteClassAsync(string className)
    {
        await Gate.WaitAsync();
        var stagedDirectories = new List<(string Original, string Staged)>();
        try
        {
            var catalog = await ReadCatalogAsync();
            var canonicalName = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate, className, StringComparison.OrdinalIgnoreCase));
            if (canonicalName is null)
                throw new InvalidOperationException("找不到要删除的 Class。");
            if (catalog.Classes.Count <= 1)
                throw new InvalidOperationException("至少需要保留一个 Class。");

            var mapsToDelete = catalog.Maps.Where(map => string.Equals(
                map.Class, canonicalName, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var map in mapsToDelete)
            {
                var original = GetMapDirectory(map.Id);
                if (!Directory.Exists(original))
                    continue;
                var staged = Path.Combine(_rootDirectory, $".delete-class-{map.Id:N}-{Guid.NewGuid():N}");
                Directory.Move(original, staged);
                stagedDirectories.Add((original, staged));
            }

            catalog.Maps.RemoveAll(map => mapsToDelete.Any(candidate => candidate.Id == map.Id));
            catalog.VariantGroups.RemoveAll(group => string.Equals(
                group.Class,
                canonicalName,
                StringComparison.OrdinalIgnoreCase));
            catalog.Classes.Remove(canonicalName);
            catalog.ClassProperties.Remove(canonicalName);
            await WriteCatalogAsync(catalog);
            foreach (var (_, staged) in stagedDirectories)
                if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            return new MapClassDeletionResult(canonicalName, mapsToDelete.Length);
        }
        catch
        {
            foreach (var (original, staged) in stagedDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(staged) && !Directory.Exists(original)) Directory.Move(staged, original);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private string GetEditableFloorImagePath(MapRecord record, string floorKey)
    {
        EnsureSafeFloorKey(floorKey);
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey);
        if (floor is not null && !string.IsNullOrWhiteSpace(floor.ImageFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ImageFileName);

        if (GetOrderedFloorPosition(record, floorKey) == 0)
            return GetFloorOnePath(record);
        if (GetOrderedFloorPosition(record, floorKey) == 1)
            return GetFloorTwoPath(record);

        var directory = GetMapDirectory(record.Id);
        var prefix = $"floor-{floorKey}";
        var existingPath = Directory.EnumerateFiles(directory, $"{prefix}.*")
            .FirstOrDefault(path => IsSupportedImage(path));
        return existingPath ?? Path.Combine(directory, $"{prefix}.png");
    }

    public string GetFloorRecognitionPath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (!string.IsNullOrWhiteSpace(floor.RecognitionFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.RecognitionFileName);

        var profile = record.Recognition.GetFloor(floorKey)
            ?? throw new InvalidOperationException($"地图楼层 '{floorKey}' 缺少识别配置。");
        return UsesWholeSourceImage(profile) && profile.BackgroundLayers.Count == 0
            ? GetFloorImagePath(record, floorKey)
            : Path.Combine(GetMapDirectory(record.Id), GetFloorRecognitionFileName(floorKey));
    }

    public string GetFloorOverlayPath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (!string.IsNullOrWhiteSpace(floor.OverlayFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.OverlayFileName);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorOverlayFileName(floorKey));
    }

    public string GetFloorThumbnailPath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (!string.IsNullOrWhiteSpace(floor.ThumbnailFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ThumbnailFileName);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorThumbnailFileName(floorKey));
    }
    public MapCatalogRevision GetCatalogRevision()
    {
        if (!File.Exists(CatalogPath))
            return MapCatalogRevision.Empty;
        var info = new FileInfo(CatalogPath);
        return new MapCatalogRevision(info.LastWriteTimeUtc.Ticks, info.Length);
    }
    public static bool IsSupportedImage(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
