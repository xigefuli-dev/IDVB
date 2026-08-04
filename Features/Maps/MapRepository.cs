using OpenCvSharp;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public readonly record struct MapCatalogRevision(long LastWriteUtcTicks, long Length)
{
    public static MapCatalogRevision Empty { get; } = new(0, 0);
}

/// <summary>
/// Persists map metadata and its two source images in the application's local data directory.
/// </summary>
public sealed class MapRepository
{
    private const int CurrentStorageSchemaVersion = 12;
    private const string FloorOneRecognitionFileName = "floor-1-recognition.png";
    private const string FloorTwoRecognitionFileName = "floor-2-recognition.png";
    private const string FloorOneOverlayFileName = "floor-1-overlay.png";
    private const string FloorTwoOverlayFileName = "floor-2-overlay.png";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new()
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
                Floors = record.Floors.Select(f => new FloorDefinition
                {
                    Key = f.Key,
                    DisplayName = f.DisplayName,
                    SortOrder = f.SortOrder
                }).ToList(),
                Class = record.Class,
                Title = record.Title,
                ContentVersion = record.ContentVersion,
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
            record.ContentVersion = existing is null
                ? Math.Max(1, draft.ContentVersion)
                : Math.Max(1, existing.ContentVersion + 1);
            record.PortableGates = draft.PortableGates
                .Select(gate => gate.Clone())
                .ToList();
            record.UpdatedAt = DateTimeOffset.UtcNow;
            draft.Recognition.EnsureStandardAnchors();
            record.Recognition = draft.Recognition.Clone();
            record.NormalizeRecognition();

            // A draft can only be saved into an existing canonical class.
            var requestedClass = NormalizeClassName(draft.Class) ?? "S1";
            record.Class = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate, requestedClass, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("所选 Class 已不存在，请返回列表后重试。");
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
            var orderedProfiles = record.Floors
                .OrderBy(floor => floor.SortOrder)
                .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                .Select(floor => record.Recognition.GetFloor(floor.Key))
                .Where(profile => profile is not null)
                .Cast<FloorRecognitionProfile>()
                .ToArray();
            if (orderedProfiles.Length > 0)
                record.Recognition.FirstFloor = orderedProfiles[0];
            if (orderedProfiles.Length > 1)
                record.Recognition.SecondFloor = orderedProfiles[1];
            record.Recognition.EnsureStandardAnchors();
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
                CreateRecognitionAssets(
                    sourcePath,
                    Path.Combine(stagingDirectory, recognitionFileName),
                    profile,
                    Path.Combine(stagingDirectory, overlayFileName));
                await PopulateDerivedImageMetadataAsync(
                    floor,
                    sourcePath,
                    Path.Combine(stagingDirectory, recognitionFileName),
                    Path.Combine(stagingDirectory, overlayFileName),
                    profile);

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

                var recognitionSourcePath = UsesWholeSourceImage(profile)
                    ? sourcePath
                    : Path.Combine(stagingDirectory, recognitionFileName);
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
                    CreateRecognitionAssets(
                        sourcePath,
                        Path.Combine(stagingDirectory, compatibilityRecognitionFileName),
                        profile,
                        Path.Combine(stagingDirectory, compatibilityOverlayFileName));
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
                    .Select(record => record.Clone()).ToArray());
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
            await WriteCatalogAsync(catalog);
            return uniqueName;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Imports package classes as newly-created local classes. A durable journal
    /// lets a later process roll back a process-interrupted import.
    /// </summary>
    public async Task<MapImportBatchResult> ImportBatchAsync(
        IReadOnlyList<MapImportClassDraft> sourceClasses,
        CancellationToken cancellationToken = default)
    {
        if (sourceClasses.Count == 0 || sourceClasses.Any(item => item.Maps.Count == 0))
            throw new InvalidOperationException("IDVM 包不包含可导入的非空 Class。");

        Directory.CreateDirectory(_rootDirectory);
        var journalPath = Path.Combine(
            _rootDirectory,
            $".idvm-import-{Guid.NewGuid():N}.json");
        var journal = new IdvmImportJournal { ProcessId = Environment.ProcessId };
        WriteImportJournal(journalPath, journal);
        var imported = new List<MapRecord>();

        try
        {
            foreach (var sourceClass in sourceClasses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localClass = await CreateUniqueClassAsync(sourceClass.SourceName, uniqueName =>
                {
                    journal.CreatedClasses.Add(uniqueName);
                    WriteImportJournal(journalPath, journal);
                });

                foreach (var sourceDraft in sourceClass.Maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sourceDraft.Id = Guid.NewGuid();
                    sourceDraft.CreateAsImportedCopy = true;
                    sourceDraft.Class = localClass;
                    journal.ImportedMapIds.Add(sourceDraft.Id.Value);
                    WriteImportJournal(journalPath, journal);
                    var saved = await SaveAsync(sourceDraft);
                    imported.Add(saved);
                }
            }

            journal.Completed = true;
            WriteImportJournal(journalPath, journal);
            File.Delete(journalPath);
            return new MapImportBatchResult(journal.CreatedClasses.ToArray(), imported.ToArray());
        }
        catch
        {
            var rolledBack = await RollBackImportAsync(journal);
            if (rolledBack && File.Exists(journalPath))
                File.Delete(journalPath);
            throw;
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
            catalog.Classes.Remove(canonicalName);
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
        return UsesWholeSourceImage(profile)
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
            ?? throw new InvalidOperationException($"鍦板浘涓嶅寘鍚ゼ灞?'{floorKey}'銆?");
        if (!string.IsNullOrWhiteSpace(floor.ThumbnailFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ThumbnailFileName);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorThumbnailFileName(floorKey));
    }

    private static int GetOrderedFloorPosition(MapRecord record, string floorKey) =>
        record.Floors
            .OrderBy(floor => floor.SortOrder)
            .ThenBy(floor => floor.Key, StringComparer.Ordinal)
            .Select((floor, index) => (floor.Key, index))
            .FirstOrDefault(pair => string.Equals(pair.Key, floorKey, StringComparison.OrdinalIgnoreCase), (Key: string.Empty, index: -1))
            .index;

    private string BuildFloorRecognitionPath(MapRecord record, string floorKey)
    {
        var profile = record.Recognition.GetFloor(floorKey);
        if (profile is not null && UsesWholeSourceImage(profile))
            return GetFloorImagePath(record, floorKey);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorRecognitionFileName(floorKey));
    }

    public MapCatalogRevision GetCatalogRevision()
    {
        if (!File.Exists(CatalogPath))
            return MapCatalogRevision.Empty;
        var info = new FileInfo(CatalogPath);
        return new MapCatalogRevision(info.LastWriteTimeUtc.Ticks, info.Length);
    }

    /// <summary>
    /// Repairs file stamps and list thumbnails without blocking catalog reads.
    /// The expensive image work is explicitly scheduled away from the UI thread.
    /// </summary>
    public async Task RepairImageMetadataAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        MapCatalogDocument catalog;
        try
        {
            catalog = await ReadCatalogAsync();
        }
        finally
        {
            Gate.Release();
        }

        var changed = false;
        foreach (var map in catalog.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var floor in MapFloorRules.GetOrderedFloors(map))
            {
                if (string.IsNullOrWhiteSpace(floor.ImageFileName))
                    continue;

                var sourcePath = GetSafeMapFilePath(
                    GetMapDirectory(map.Id),
                    floor.ImageFileName);
                if (!File.Exists(sourcePath))
                    continue;

                if (!HasMatchingFileStamp(
                        sourcePath,
                        floor.ImageFileLength,
                        floor.ImageLastWriteUtcTicks)
                    || string.IsNullOrWhiteSpace(floor.ImageSha256)
                    || floor.ImageWidth <= 0
                    || floor.ImageHeight <= 0)
                {
                    var metadata = await Task.Run(
                        () => ReadImageMetadataAsync(sourcePath),
                        cancellationToken);
                    floor.ImageSha256 = metadata.Sha256;
                    floor.ImageWidth = metadata.Width;
                    floor.ImageHeight = metadata.Height;
                    floor.ImageFileLength = metadata.FileLength;
                    floor.ImageLastWriteUtcTicks = metadata.LastWriteUtcTicks;
                    changed = true;
                }

                var recognitionPath = GetFloorRecognitionPath(map, floor.Key);
                if (!File.Exists(recognitionPath))
                    recognitionPath = sourcePath;
                var thumbnailPath = GetFloorThumbnailPath(map, floor.Key);
                if (File.Exists(recognitionPath)
                    && (!HasMatchingFileStamp(
                            thumbnailPath,
                            floor.ThumbnailFileLength,
                            floor.ThumbnailLastWriteUtcTicks)
                        || floor.ThumbnailWidth <= 0
                        || floor.ThumbnailHeight <= 0))
                {
                    await CreateThumbnailAsync(recognitionPath, thumbnailPath);
                    await PopulateThumbnailMetadataAsync(floor, thumbnailPath);
                    changed = true;
                }
            }
        }

        if (!changed && catalog.StorageSchemaVersion >= CurrentStorageSchemaVersion)
            return;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var latest = await ReadCatalogAsync();
            foreach (var repairedMap in catalog.Maps)
            {
                var latestMap = latest.Maps.FirstOrDefault(map => map.Id == repairedMap.Id);
                if (latestMap is null)
                    continue;
                foreach (var repairedFloor in repairedMap.Floors)
                {
                    var latestFloor = latestMap.Floors.FirstOrDefault(
                        floor => string.Equals(floor.Key, repairedFloor.Key, StringComparison.Ordinal));
                    if (latestFloor is null
                        || !string.Equals(latestFloor.ImageFileName, repairedFloor.ImageFileName, StringComparison.Ordinal))
                        continue;
                    CopyRepairedMetadata(repairedFloor, latestFloor);
                }
            }
            latest.StorageSchemaVersion = CurrentStorageSchemaVersion;
            await WriteCatalogAsync(latest);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task EnsureDerivedAssetsAsync(IReadOnlyList<MapRecord> maps)
    {
        var assetsChanged = await Task.Run(() =>
            {
                var changed = false;
                foreach (var map in maps)
                {
                    map.NormalizeRecognition();
                    if (!map.Recognition.HasRequiredIdentificationData())
                        continue;

                    foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                    {
                        var profile = MapFloorRules.GetFloorProfile(map, floor.Key);
                        if (profile is null)
                            continue;

                        var previousWidth = profile.RecognitionPixelWidth;
                        var previousHeight = profile.RecognitionPixelHeight;
                        var sourcePath = GetFloorImagePath(map, floor.Key);
                        var recognitionPath = GetFloorRecognitionPath(map, floor.Key);
                        var overlayPath = GetFloorOverlayPath(map, floor.Key);
                        if (!File.Exists(sourcePath))
                            continue;

                        var recognitionMatches = MatchesStoredDerivedMetadata(
                            recognitionPath,
                            floor.RecognitionSha256,
                            floor.RecognitionWidth,
                            floor.RecognitionHeight,
                            floor.RecognitionFileLength,
                            floor.RecognitionLastWriteUtcTicks,
                            floor.ImageSha256,
                            UsesWholeSourceImage(profile));
                        recognitionMatches &= string.Equals(
                            floor.RecognitionSourceSha256,
                            floor.ImageSha256,
                            StringComparison.OrdinalIgnoreCase);
                        var overlayMatches = MatchesStoredDerivedMetadata(
                            overlayPath,
                            floor.OverlaySha256,
                            floor.OverlayWidth,
                            floor.OverlayHeight,
                            floor.OverlayFileLength,
                            floor.OverlayLastWriteUtcTicks,
                            floor.RecognitionSha256,
                            requiresFile: true)
                            && string.Equals(
                                floor.OverlaySourceSha256,
                                floor.RecognitionSha256,
                                StringComparison.OrdinalIgnoreCase);
                        if (!recognitionMatches
                            || !overlayMatches
                            || floor.RecognitionWidth != profile.RecognitionPixelWidth
                            || floor.RecognitionHeight != profile.RecognitionPixelHeight)
                        {
                            CreateRecognitionAssets(
                                sourcePath,
                                recognitionPath,
                                profile,
                                overlayPath);
                            PopulateDerivedImageMetadataAsync(
                                floor,
                                sourcePath,
                                recognitionPath,
                                overlayPath,
                                profile).GetAwaiter().GetResult();
                            changed = true;
                        }

                        changed |= previousWidth != profile.RecognitionPixelWidth
                            || previousHeight != profile.RecognitionPixelHeight;
                    }
                }
                return changed;
            });

        if (assetsChanged)
        {
            await Gate.WaitAsync();
            try
            {
                var catalog = await ReadCatalogAsync();
                foreach (var map in maps)
                {
                    var stored = catalog.Maps.FirstOrDefault(candidate => candidate.Id == map.Id);
                    if (stored is null)
                        continue;
                    foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                    {
                        var sourceProfile = MapFloorRules.GetFloorProfile(map, floor.Key);
                        var storedProfile = MapFloorRules.GetFloorProfile(stored, floor.Key);
                        if (sourceProfile is null || storedProfile is null)
                            continue;
                        storedProfile.RecognitionPixelWidth = sourceProfile.RecognitionPixelWidth;
                        storedProfile.RecognitionPixelHeight = sourceProfile.RecognitionPixelHeight;
                        storedProfile.ValidMapBounds = sourceProfile.ValidMapBounds?.Clone();

                        var storedFloor = stored.Floors.FirstOrDefault(
                            candidate => string.Equals(
                                candidate.Key,
                                floor.Key,
                                StringComparison.Ordinal));
                        if (storedFloor is not null)
                        {
                            var sourceFloor = map.Floors.First(candidate => string.Equals(
                                candidate.Key,
                                floor.Key,
                                StringComparison.Ordinal));
                            storedFloor.ImageFileName = sourceFloor.ImageFileName;
                            storedFloor.ImageSha256 = sourceFloor.ImageSha256;
                            storedFloor.ImageWidth = sourceFloor.ImageWidth;
                            storedFloor.ImageHeight = sourceFloor.ImageHeight;
                            storedFloor.ImageFileLength = sourceFloor.ImageFileLength;
                            storedFloor.ImageLastWriteUtcTicks = sourceFloor.ImageLastWriteUtcTicks;
                            storedFloor.RecognitionFileName = sourceFloor.RecognitionFileName;
                            storedFloor.RecognitionSha256 = sourceFloor.RecognitionSha256;
                            storedFloor.RecognitionSourceSha256 = sourceFloor.RecognitionSourceSha256;
                            storedFloor.RecognitionWidth = sourceFloor.RecognitionWidth;
                            storedFloor.RecognitionHeight = sourceFloor.RecognitionHeight;
                            storedFloor.RecognitionFileLength = sourceFloor.RecognitionFileLength;
                            storedFloor.RecognitionLastWriteUtcTicks = sourceFloor.RecognitionLastWriteUtcTicks;
                            storedFloor.OverlayFileName = sourceFloor.OverlayFileName;
                            storedFloor.OverlaySha256 = sourceFloor.OverlaySha256;
                            storedFloor.OverlaySourceSha256 = sourceFloor.OverlaySourceSha256;
                            storedFloor.OverlayWidth = sourceFloor.OverlayWidth;
                            storedFloor.OverlayHeight = sourceFloor.OverlayHeight;
                            storedFloor.OverlayFileLength = sourceFloor.OverlayFileLength;
                            storedFloor.OverlayLastWriteUtcTicks = sourceFloor.OverlayLastWriteUtcTicks;
                        }
                    }
                }
                await WriteCatalogAsync(catalog);
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    public static bool IsSupportedImage(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

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

    internal static string BuildUniqueImportedClassName(
        string sourceName,
        IEnumerable<string> existingNames)
    {
        var occupied = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!occupied.Contains(sourceName))
            return sourceName;
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{sourceName} - 新添加{suffix}";
            if (!occupied.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException("无法为导入的 Class 分配唯一名称。");
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

    private static async Task<string> CopyImageToDirectoryAsync(string sourcePath, string destinationDirectory, string filePrefix)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var fileName = $"{filePrefix}{extension}";
        var targetPath = Path.Combine(destinationDirectory, fileName);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(targetPath);
        await source.CopyToAsync(destination);
        return fileName;
    }

    private static string GetFloorImageFilePrefix(string floorKey) =>
        floorKey switch
        {
            "1f" => "floor-1",
            "2f" => "floor-2",
            _ => $"floor-{floorKey}"
        };

    private static string GetFloorRecognitionFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => FloorOneRecognitionFileName,
            "2f" => FloorTwoRecognitionFileName,
            _ => $"floor-{floorKey}-recognition.png"
        };

    private static string GetFloorOverlayFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => FloorOneOverlayFileName,
            "2f" => FloorTwoOverlayFileName,
            _ => $"floor-{floorKey}-overlay.png"
        };

    private static string GetFloorThumbnailFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => "floor-1-thumbnail.jpg",
            "2f" => "floor-2-thumbnail.jpg",
            _ => $"floor-{floorKey}-thumbnail.jpg"
        };

    private static Task CreateThumbnailAsync(string sourcePath, string destinationPath) =>
        Task.Run(() =>
        {
            using var source = Cv2.ImRead(sourcePath, ImreadModes.Unchanged);
            if (source.Empty())
                throw new InvalidOperationException($"Image cannot be read: '{sourcePath}'.");

            const int maxWidth = 400;
            var width = Math.Min(maxWidth, source.Width);
            var height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
            using var thumbnail = new Mat();
            Cv2.Resize(source, thumbnail, new Size(width, height), 0, 0, InterpolationFlags.Area);
            if (!Cv2.ImWrite(destinationPath, thumbnail, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]))
                throw new InvalidOperationException($"Image cannot be written: '{destinationPath}'.");
        });

    private static async Task PopulateThumbnailMetadataAsync(
        FloorDefinition floor,
        string thumbnailPath)
    {
        var metadata = await Task.Run(() => ReadImageMetadataAsync(thumbnailPath));
        floor.ThumbnailFileName = Path.GetFileName(thumbnailPath);
        floor.ThumbnailSha256 = metadata.Sha256;
        floor.ThumbnailWidth = metadata.Width;
        floor.ThumbnailHeight = metadata.Height;
        floor.ThumbnailFileLength = metadata.FileLength;
        floor.ThumbnailLastWriteUtcTicks = metadata.LastWriteUtcTicks;
    }

    private static void CopyRepairedMetadata(
        FloorDefinition source,
        FloorDefinition destination)
    {
        if (destination.ImageFileLength <= 0
            || destination.ImageLastWriteUtcTicks <= 0)
        {
            destination.ImageSha256 = source.ImageSha256;
            destination.ImageWidth = source.ImageWidth;
            destination.ImageHeight = source.ImageHeight;
            destination.ImageFileLength = source.ImageFileLength;
            destination.ImageLastWriteUtcTicks = source.ImageLastWriteUtcTicks;
        }

        if (destination.ThumbnailFileLength <= 0
            || destination.ThumbnailLastWriteUtcTicks <= 0)
        {
            destination.ThumbnailFileName = source.ThumbnailFileName;
            destination.ThumbnailSha256 = source.ThumbnailSha256;
            destination.ThumbnailWidth = source.ThumbnailWidth;
            destination.ThumbnailHeight = source.ThumbnailHeight;
            destination.ThumbnailFileLength = source.ThumbnailFileLength;
            destination.ThumbnailLastWriteUtcTicks = source.ThumbnailLastWriteUtcTicks;
        }
    }

    private static async Task PopulateFloorImageMetadataAsync(
        MapRecord record,
        string stagingDirectory,
        IReadOnlyDictionary<string, string> floorImageFileNames)
    {
        foreach (var floor in MapFloorRules.GetOrderedFloors(record))
        {
            if (!floorImageFileNames.TryGetValue(floor.Key, out var fileName))
            {
                throw new InvalidOperationException(
                    $"Floor '{floor.Key}' has no copied local image.");
            }

            var path = Path.Combine(stagingDirectory, fileName);
            var metadata = await ReadImageMetadataAsync(path);
            floor.ImageFileName = fileName;
            floor.ImageSha256 = metadata.Sha256;
            floor.ImageWidth = metadata.Width;
            floor.ImageHeight = metadata.Height;
            floor.ImageFileLength = metadata.FileLength;
            floor.ImageLastWriteUtcTicks = metadata.LastWriteUtcTicks;
        }
    }

    private static bool MatchesStoredDerivedMetadata(
        string path,
        string expectedSha256,
        int expectedWidth,
        int expectedHeight,
        long expectedFileLength,
        long expectedLastWriteUtcTicks,
        string expectedSourceSha256,
        bool requiresFile)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || string.IsNullOrWhiteSpace(expectedSourceSha256)
            || expectedWidth <= 0
            || expectedHeight <= 0)
            return false;
        if (requiresFile && !File.Exists(path))
            return false;
        if (!File.Exists(path))
            return string.Equals(path, expectedSourceSha256, StringComparison.OrdinalIgnoreCase);

        var info = new FileInfo(path);
        if (expectedFileLength > 0
            && expectedLastWriteUtcTicks > 0
            && info.Length == expectedFileLength
            && info.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks)
            return true;

        var actual = ReadImageMetadataAsync(path).GetAwaiter().GetResult();
        return string.Equals(actual.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            && actual.Width == expectedWidth
            && actual.Height == expectedHeight;
    }

    private static async Task PopulateDerivedImageMetadataAsync(
        FloorDefinition floor,
        string sourcePath,
        string recognitionPath,
        string overlayPath,
        FloorRecognitionProfile profile)
    {
        var sourceMetadata = await ReadImageMetadataAsync(sourcePath);
        var recognitionPathForMetadata = UsesWholeSourceImage(profile)
            ? sourcePath
            : recognitionPath;
        var recognitionMetadata = await ReadImageMetadataAsync(recognitionPathForMetadata);
        var overlayMetadata = await ReadImageMetadataAsync(overlayPath);

        floor.RecognitionFileName = Path.GetFileName(recognitionPathForMetadata);
        floor.RecognitionSha256 = recognitionMetadata.Sha256;
        floor.RecognitionSourceSha256 = sourceMetadata.Sha256;
        floor.RecognitionWidth = recognitionMetadata.Width;
        floor.RecognitionHeight = recognitionMetadata.Height;
        floor.RecognitionFileLength = recognitionMetadata.FileLength;
        floor.RecognitionLastWriteUtcTicks = recognitionMetadata.LastWriteUtcTicks;
        floor.OverlayFileName = Path.GetFileName(overlayPath);
        floor.OverlaySha256 = overlayMetadata.Sha256;
        floor.OverlaySourceSha256 = recognitionMetadata.Sha256;
        floor.OverlayWidth = overlayMetadata.Width;
        floor.OverlayHeight = overlayMetadata.Height;
        floor.OverlayFileLength = overlayMetadata.FileLength;
        floor.OverlayLastWriteUtcTicks = overlayMetadata.LastWriteUtcTicks;
    }

    private static async Task<FloorImageMetadata> ReadImageMetadataAsync(string path)
    {
        using var image = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (image.Empty())
            throw new InvalidOperationException($"Image cannot be decoded: '{path}'.");

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        var info = new FileInfo(path);
        return new FloorImageMetadata(
            Convert.ToHexString(hash).ToLowerInvariant(),
            image.Width,
            image.Height,
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    private readonly record struct FloorImageMetadata(
        string Sha256,
        int Width,
        int Height,
        long FileLength,
        long LastWriteUtcTicks);

    private static void CreateRecognitionAssets(
        string sourcePath,
        string destinationPath,
        FloorRecognitionProfile profile,
        string? overlayPath)
    {
        using var source = Cv2.ImRead(sourcePath, ImreadModes.Unchanged);
        if (source.Empty())
            throw new InvalidOperationException("无法读取地图原图以生成识别区域。");
        var usesWholeSource = UsesWholeSourceImage(profile);
        using var recognition = usesWholeSource
            ? source.Clone()
            : new Mat(source, GetPixelRegion(profile.GetEffectiveRecognitionRegion(), source.Width, source.Height));
        profile.RecognitionPixelWidth = recognition.Width;
        profile.RecognitionPixelHeight = recognition.Height;
        if (profile.ValidMapBounds?.IsValid is not true)
        {
            profile.ValidMapBounds = MapReferenceBounds.FullImage(
                recognition.Width,
                recognition.Height);
        }
        if (!usesWholeSource && !Cv2.ImWrite(destinationPath, recognition))
            throw new InvalidOperationException("无法保存地图识别区域。");
        if (overlayPath is not null)
            CreateWhiteKeyOverlay(recognition, overlayPath);
    }

    private static Rect GetPixelRegion(NormalizedRectangle region, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(region.X * width), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling((region.X + region.Width) * width),
            left + 1,
            width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((region.Y + region.Height) * height),
            top + 1,
            height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static void CreateWhiteKeyOverlay(Mat source, string destinationPath)
    {
        using var bgra = new Mat();
        switch (source.Channels())
        {
            case 4:
                source.CopyTo(bgra);
                break;
            case 3:
                Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
                break;
            default:
                Cv2.CvtColor(source, bgra, ColorConversionCodes.GRAY2BGRA);
                break;
        }

        using var bgr = new Mat();
        using var hsv = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var bgraChannels = Cv2.Split(bgra);
        var hsvChannels = Cv2.Split(hsv);
        try
        {
            using var neutralMask = new Mat();
            using var whiteness = new Mat();
            using var alphaReduction = new Mat();
            using var generatedAlpha = new Mat();
            using var keyedAlpha = new Mat();
            using var finalAlpha = bgraChannels[3].Clone();
            Cv2.InRange(hsvChannels[1], new Scalar(0), new Scalar(25), neutralMask);
            Cv2.Subtract(hsvChannels[2], new Scalar(230), whiteness);
            whiteness.ConvertTo(alphaReduction, MatType.CV_8UC1, 255d / 15d);
            Cv2.Subtract(new Scalar(255), alphaReduction, generatedAlpha);
            Cv2.Min(bgraChannels[3], generatedAlpha, keyedAlpha);
            keyedAlpha.CopyTo(finalAlpha, neutralMask);

            using var result = new Mat();
            Cv2.Merge([bgraChannels[0], bgraChannels[1], bgraChannels[2], finalAlpha], result);
            if (!Cv2.ImWrite(destinationPath, result))
                throw new InvalidOperationException("无法保存透明地图图层。");
        }
        finally
        {
            foreach (var channel in bgraChannels)
                channel.Dispose();
            foreach (var channel in hsvChannels)
                channel.Dispose();
        }
    }

    // ── 侧门特征 ──────────────────────────────────────────────────────

    /// <summary>获取侧门特征图文件名（相对 map 目录）。</summary>
    private static string GetSideEntranceFeatureFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => "floor-1-side-entrance-feature.png",
            "2f" => "floor-2-side-entrance-feature.png",
            _ => $"floor-{floorKey}-side-entrance-feature.png"
        };

    /// <summary>获取侧门特征图的完整磁盘路径。</summary>
    public string GetSideEntranceFeaturePath(MapRecord record, string floorKey)
    {
        var profile = record.Recognition.GetFloor(floorKey);
        if (profile is not null
            && !string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
        {
            return GetSafeMapFilePath(
                GetMapDirectory(record.Id),
                profile.SideEntranceFeatureFileName);
        }

        return Path.Combine(
            GetMapDirectory(record.Id),
            GetSideEntranceFeatureFileName(floorKey));
    }

    /// <summary>
    /// 若侧门锚点已标注，为该楼层生成侧门特征图并更新 profile 的相关字段。
    /// 若锚点未标注或识别图不存在，则静默跳过。
    /// </summary>
    private async Task TryGenerateSideEntranceFeatureAsync(
        string stagingDirectory,
        FloorRecognitionProfile profile,
        int featureRadius)
    {
        var sideAnchor = profile.FindAnchor("side-entrance");
        if (sideAnchor?.IsMarked is not true)
            return;

        // 找到已在 staging 中写好的识别图路径
        var recognitionFileName = GetFloorRecognitionFileName(profile.FloorKey);
        var recognitionPath = Path.Combine(stagingDirectory, recognitionFileName);
        if (!File.Exists(recognitionPath))
            return;

        try
        {
            using var recognitionMat = Cv2.ImRead(recognitionPath, ImreadModes.Grayscale);
            if (recognitionMat.Empty())
                return;

            using var result = _sideEntrancePreprocessor.Process(
                recognitionMat, sideAnchor.Bounds!, featureRadius);

            var featureFileName = GetSideEntranceFeatureFileName(profile.FloorKey);
            var featurePath = Path.Combine(stagingDirectory, featureFileName);
            if (!Cv2.ImWrite(featurePath, result.Feature))
                return;

            // 计算特征图和源识别图的 SHA-256
            await using var featureStream = File.OpenRead(featurePath);
            var featureHash = await SHA256.HashDataAsync(featureStream);
            await using var sourceStream = File.OpenRead(recognitionPath);
            var sourceHash = await SHA256.HashDataAsync(sourceStream);

            profile.SideEntranceFeatureFileName = featureFileName;
            profile.SideEntranceFeatureSha256 =
                Convert.ToHexString(featureHash).ToLowerInvariant();
            profile.SideEntranceFeatureSourceSha256 =
                Convert.ToHexString(sourceHash).ToLowerInvariant();
            profile.SideEntranceFeatureCenterX = result.CenterX;
            profile.SideEntranceFeatureCenterY = result.CenterY;
            profile.SideEntranceFeatureRadius   = result.Radius;
        }
        catch
        {
            // 特征生成失败不阻断主保存流程；下次打开编辑页面或批量重建时会重试
        }
    }

    /// <summary>
    /// 批量为所有地图重新生成侧门特征图（半径参数改变时调用）。
    /// 每张地图处理完毕后通知进度；出错地图跳过。
    /// </summary>
    public async Task RebuildAllSideEntranceFeaturesAsync(
        int featureRadius,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        featureRadius = Math.Clamp(featureRadius, 20, 500);
        await Gate.WaitAsync(cancellationToken);
        MapCatalogDocument catalog;
        try
        {
            catalog = await ReadCatalogAsync();
        }
        finally
        {
            Gate.Release();
        }

        var maps = catalog.Maps;
        var total = maps.Count;
        var done  = 0;

        foreach (var record in maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;
            var mapDirectory = GetMapDirectory(record.Id);

            foreach (var floorDef in MapFloorRules.GetOrderedFloors(record))
            {
                var profile = MapFloorRules.GetFloorProfile(record, floorDef.Key);
                if (profile is null)
                    continue;
                var sideAnchor = profile.FindAnchor("side-entrance");
                if (sideAnchor?.IsMarked is not true)
                    continue;

                var recognitionPath = GetFloorRecognitionPath(record, floorDef.Key);
                if (!File.Exists(recognitionPath))
                    continue;

                try
                {
                    using var recognitionMat = Cv2.ImRead(recognitionPath, ImreadModes.Grayscale);
                    if (recognitionMat.Empty())
                        continue;

                    using var result = _sideEntrancePreprocessor.Process(
                        recognitionMat, sideAnchor.Bounds!, featureRadius);

                    var featureFileName = GetSideEntranceFeatureFileName(floorDef.Key);
                    var featurePath = Path.Combine(mapDirectory, featureFileName);
                    if (!Cv2.ImWrite(featurePath, result.Feature))
                        continue;

                    await using var featureStream = File.OpenRead(featurePath);
                    var featureHash = await SHA256.HashDataAsync(featureStream, cancellationToken);
                    await using var sourceStream = File.OpenRead(recognitionPath);
                    var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);

                    profile.SideEntranceFeatureFileName = featureFileName;
                    profile.SideEntranceFeatureSha256 =
                        Convert.ToHexString(featureHash).ToLowerInvariant();
                    profile.SideEntranceFeatureSourceSha256 =
                        Convert.ToHexString(sourceHash).ToLowerInvariant();
                    profile.SideEntranceFeatureCenterX = result.CenterX;
                    profile.SideEntranceFeatureCenterY = result.CenterY;
                    profile.SideEntranceFeatureRadius   = result.Radius;

                    // 同步到 Floors 字典
                    record.Recognition.Floors[floorDef.Key] = profile;
                    changed = true;
                }
                catch
                {
                    // 单张地图出错跳过，不影响其他地图
                }
            }

            if (changed)
            {
                await Gate.WaitAsync(cancellationToken);
                try
                {
                    var liveCatalog = await ReadCatalogAsync();
                    var stored = liveCatalog.Maps.FirstOrDefault(m => m.Id == record.Id);
                    if (stored is not null)
                    {
                        foreach (var floorDef in MapFloorRules.GetOrderedFloors(record))
                        {
                            var srcProfile = MapFloorRules.GetFloorProfile(record, floorDef.Key);
                            var dstProfile = MapFloorRules.GetFloorProfile(stored, floorDef.Key);
                            if (srcProfile is null || dstProfile is null)
                                continue;
                            dstProfile.SideEntranceFeatureFileName =
                                srcProfile.SideEntranceFeatureFileName;
                            dstProfile.SideEntranceFeatureSha256 =
                                srcProfile.SideEntranceFeatureSha256;
                            dstProfile.SideEntranceFeatureSourceSha256 =
                                srcProfile.SideEntranceFeatureSourceSha256;
                            dstProfile.SideEntranceFeatureCenterX =
                                srcProfile.SideEntranceFeatureCenterX;
                            dstProfile.SideEntranceFeatureCenterY =
                                srcProfile.SideEntranceFeatureCenterY;
                            dstProfile.SideEntranceFeatureRadius =
                                srcProfile.SideEntranceFeatureRadius;
                            stored.Recognition.Floors[floorDef.Key] = dstProfile;
                        }
                        stored.Recognition.EnsureStandardAnchors();
                    }
                    await WriteCatalogAsync(liveCatalog);
                }
                finally
                {
                    Gate.Release();
                }
            }

            progress?.Report((++done, total));
        }
    }

    /// <summary>
    /// IDVM 导入场景：将包内已预计算的侧门特征图复制到 staging，并写入 SHA-256 元数据。
    /// </summary>
    private static async Task CopySideEntranceFeatureAsync(
        string importedFeaturePath,
        string stagingDirectory,
        FloorRecognitionProfile profile)
    {
        try
        {
            var featureFileName = GetSideEntranceFeatureFileName(profile.FloorKey);
            var featureTarget = Path.Combine(stagingDirectory, featureFileName);
            await using (var src = File.OpenRead(importedFeaturePath))
            await using (var dst = File.Create(featureTarget))
                await src.CopyToAsync(dst);

            await using var hashStream = File.OpenRead(featureTarget);
            var hash = await SHA256.HashDataAsync(hashStream);
            profile.SideEntranceFeatureFileName = featureFileName;
            profile.SideEntranceFeatureSha256   =
                Convert.ToHexString(hash).ToLowerInvariant();
            // SideEntranceFeatureCenterX/Y/Radius 已由 IDVM 导入者填写
        }
        catch
        {
            // 复制失败不阻断主保存流程
        }
    }

    private static bool UsesWholeSourceImage(FloorRecognitionProfile profile)
    {
        var region = profile.RecognitionRegion;
        return region?.IsValid is not true
            || (region.X <= 0.000001d
                && region.Y <= 0.000001d
                && region.X + region.Width >= 0.999999d
                && region.Y + region.Height >= 0.999999d);
    }

    private string GetMapDirectory(Guid id) => Path.Combine(_rootDirectory, id.ToString("N"));

    private string GetStoredFloorImagePath(Guid mapId, string? fileName, string fallbackPrefix)
    {
        var directory = GetMapDirectory(mapId);
        if (!string.IsNullOrWhiteSpace(fileName))
            return GetSafeMapFilePath(directory, fileName);

        var existing = GetLocalImageCandidates(directory, fallbackPrefix).FirstOrDefault();
        return existing ?? Path.Combine(directory, $"{fallbackPrefix}.png");
    }

    private static IReadOnlyList<string> GetLocalImageCandidates(
        string directory,
        string filePrefix)
    {
        return !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, $"{filePrefix}.*")
                .Where(IsSupportedImage)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static string GetSafeMapFilePath(string mapDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || fileName.Contains('\\')
            || fileName.Contains('/')
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Map image file name is invalid: '{fileName}'.");
        }

        var directory = Path.GetFullPath(mapDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(mapDirectory, fileName));
        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Map image path escapes the map directory: '{fileName}'.");
        return fullPath;
    }

    private static void EnsureSafeFloorKey(string floorKey)
    {
        if (string.IsNullOrWhiteSpace(floorKey)
            || floorKey.Contains('\\')
            || floorKey.Contains('/')
            || !string.Equals(Path.GetFileName(floorKey), floorKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Floor key is invalid: '{floorKey}'.");
        }
    }

    private void ValidateFloorDefinitions(MapRecord record)
    {
        var floors = MapFloorRules.GetOrderedFloors(record);
        if (floors.Count == 0)
            throw new InvalidOperationException($"Map {record.Id} has no floor definitions.");

        if (floors.Count != record.Floors.Count)
            throw new InvalidOperationException($"Map {record.Id} has an invalid floor definition list.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var sortOrders = new HashSet<int>();
        for (var index = 0; index < floors.Count; index++)
        {
            var floor = floors[index];
            EnsureSafeFloorKey(floor.Key);
            if (!keys.Add(floor.Key) || !sortOrders.Add(floor.SortOrder))
                throw new InvalidOperationException(
                    $"Map {record.Id} has duplicate floor key or sort order for '{floor.Key}'.");
            if (floor.SortOrder != index + 1)
                throw new InvalidOperationException(
                    $"Map {record.Id} has a non-contiguous floor sort order at '{floor.Key}'.");

            var profile = record.Recognition.GetFloor(floor.Key);
            if (profile is null
                || !string.Equals(profile.FloorKey, floor.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' has no matching recognition profile.");
            }
        }

        if (!string.Equals(
                record.Recognition.FirstFloor.FloorKey,
                floors[0].Key,
                StringComparison.Ordinal)
            || (floors.Count > 1
                && !string.Equals(
                    record.Recognition.SecondFloor.FloorKey,
                    floors[1].Key,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Map {record.Id} has floor order and compatibility recognition profiles out of sync.");
        }
    }

    private async Task MigrateFloorImageBindingsAsync(MapRecord record)
    {
        ValidateFloorDefinitions(record);
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedFloors = MapFloorRules.GetOrderedFloors(record);

        foreach (var floor in orderedFloors)
        {
            var path = ResolveLegacyFloorImagePath(record, floor);
            var fileName = Path.GetFileName(path);
            if (!usedFiles.Add(fileName))
                throw new InvalidOperationException(
                    $"Map {record.Id} maps multiple floors to the same image file '{fileName}'.");

            var metadata = await ReadImageMetadataAsync(path);
            floor.ImageFileName = fileName;
            floor.ImageSha256 = metadata.Sha256;
            floor.ImageWidth = metadata.Width;
            floor.ImageHeight = metadata.Height;
            floor.ImageFileLength = metadata.FileLength;
            floor.ImageLastWriteUtcTicks = metadata.LastWriteUtcTicks;
        }

        // V8 did not persist the relationship between a source image and its
        // generated recognition/overlay assets. Rebuild these deterministic
        // derived files from the now-explicit source binding before writing V9.
        foreach (var floor in orderedFloors)
        {
            var profile = record.Recognition.GetFloor(floor.Key)
                ?? throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' has no recognition profile.");
            var sourcePath = GetSafeMapFilePath(
                GetMapDirectory(record.Id),
                floor.ImageFileName);
            var recognitionPath = Path.Combine(
                GetMapDirectory(record.Id),
                GetFloorRecognitionFileName(floor.Key));
            var overlayPath = Path.Combine(
                GetMapDirectory(record.Id),
                GetFloorOverlayFileName(floor.Key));
            // Use a profile clone so migration records the derived asset
            // binding without changing legacy recognition dimensions until
            // the normal derived-asset repair pass runs.
            var assetProfile = profile.Clone();
            CreateRecognitionAssets(sourcePath, recognitionPath, assetProfile, overlayPath);
            await PopulateDerivedImageMetadataAsync(
                floor,
                sourcePath,
                recognitionPath,
                overlayPath,
                assetProfile);
        }

        if (orderedFloors.Count > 0)
            record.FloorOneFileName = orderedFloors[0].ImageFileName;
        if (orderedFloors.Count > 1)
            record.FloorTwoFileName = orderedFloors[1].ImageFileName;
    }

    private Task VerifyFloorImageBindingsAsync(MapRecord record) =>
        Task.Run(() => VerifyFloorImageBindings(record));

    private void VerifyFloorImageBindings(MapRecord record)
    {
        ValidateFloorDefinitions(record);
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var floor in MapFloorRules.GetOrderedFloors(record))
        {
            if (string.IsNullOrWhiteSpace(floor.ImageFileName))
                throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' has no explicit image file binding.");

            var path = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ImageFileName);
            if (!usedFiles.Add(floor.ImageFileName))
                throw new InvalidOperationException(
                    $"Map {record.Id} maps multiple floors to the same image file '{floor.ImageFileName}'.");
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' is missing its local image: '{path}'.");

            if (HasMatchingFileStamp(
                path,
                floor.ImageFileLength,
                floor.ImageLastWriteUtcTicks))
            {
                ValidateStoredDerivedBinding(record, floor);
                continue;
            }

            var actual = ReadImageMetadataAsync(path).GetAwaiter().GetResult();
            if (!string.Equals(actual.Sha256, floor.ImageSha256, StringComparison.OrdinalIgnoreCase)
                || actual.Width != floor.ImageWidth
                || actual.Height != floor.ImageHeight)
            {
                throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' image metadata does not match '{floor.ImageFileName}'.");
            }

            ValidateStoredDerivedBinding(record, floor);
        }
    }

    private void ValidateFloorBindingsFast(MapRecord record)
    {
        ValidateFloorDefinitions(record);
        foreach (var floor in MapFloorRules.GetOrderedFloors(record))
        {
            EnsureSafeFloorKey(floor.Key);
            if (!string.IsNullOrWhiteSpace(floor.ImageFileName))
                _ = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ImageFileName);
            if (!string.IsNullOrWhiteSpace(floor.RecognitionFileName))
                _ = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.RecognitionFileName);
            if (!string.IsNullOrWhiteSpace(floor.OverlayFileName))
                _ = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.OverlayFileName);
            if (!string.IsNullOrWhiteSpace(floor.ThumbnailFileName))
                _ = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ThumbnailFileName);
        }
    }

    private static bool HasMatchingFileStamp(
        string path,
        long expectedFileLength,
        long expectedLastWriteUtcTicks)
    {
        if (expectedFileLength <= 0 || expectedLastWriteUtcTicks <= 0 || !File.Exists(path))
            return false;
        var info = new FileInfo(path);
        return info.Length == expectedFileLength
            && info.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks;
    }

    private void ValidateStoredDerivedBinding(
        MapRecord record,
        FloorDefinition floor)
    {
        if (string.IsNullOrWhiteSpace(floor.RecognitionFileName)
            || string.IsNullOrWhiteSpace(floor.RecognitionSha256)
            || string.IsNullOrWhiteSpace(floor.RecognitionSourceSha256)
            || floor.RecognitionWidth <= 0
            || floor.RecognitionHeight <= 0
            || string.IsNullOrWhiteSpace(floor.OverlayFileName)
            || string.IsNullOrWhiteSpace(floor.OverlaySha256)
            || string.IsNullOrWhiteSpace(floor.OverlaySourceSha256)
            || floor.OverlayWidth <= 0
            || floor.OverlayHeight <= 0)
        {
            throw new InvalidOperationException(
                $"Map {record.Id}, floor '{floor.Key}' has incomplete derived image bindings.");
        }

        _ = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.RecognitionFileName);
        _ = GetSafeMapFilePath(GetMapDirectory(record.Id), floor.OverlayFileName);
    }

    private string ResolveLegacyFloorImagePath(MapRecord record, FloorDefinition floor)
    {
        var position = GetOrderedFloorPosition(record, floor.Key);
        var storedFileName = position switch
        {
            0 => record.FloorOneFileName,
            1 => record.FloorTwoFileName,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(storedFileName))
        {
            var storedPath = GetSafeMapFilePath(GetMapDirectory(record.Id), storedFileName);
            if (!File.Exists(storedPath))
                throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' is missing its local image: '{storedPath}'.");
            ValidateReadableImage(storedPath, $"Map {record.Id}, floor '{floor.Key}'");
            return storedPath;
        }

        var prefix = position switch
        {
            0 => "floor-1",
            1 => "floor-2",
            _ => $"floor-{floor.Key}"
        };
        var candidates = GetLocalImageCandidates(GetMapDirectory(record.Id), prefix);
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Map {record.Id}, floor '{floor.Key}' is missing its local image. "
                + "The image will not be recovered from inline catalog data.");
        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"Map {record.Id}, floor '{floor.Key}' has multiple candidate images: "
                + string.Join(", ", candidates.Select(Path.GetFileName)));

        ValidateReadableImage(candidates[0], $"Map {record.Id}, floor '{floor.Key}'");
        return candidates[0];
    }

    private static void ValidateReadableImage(string path, string context)
    {
        if (!IsSupportedImage(path))
            throw new InvalidOperationException(
                $"{context} uses an unsupported image format: '{path}'. Use PNG, JPG, or JPEG.");

        using var image = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (image.Empty())
            throw new InvalidOperationException($"{context} image cannot be decoded: '{path}'.");
    }

    private async Task<bool> RollBackImportAsync(IdvmImportJournal journal)
    {
        var succeeded = true;
        foreach (var mapId in journal.ImportedMapIds.AsEnumerable().Reverse())
        {
            try
            {
                if ((await GetMapsAsync()).Any(map => map.Id == mapId))
                    await DeleteAsync(mapId);
            }
            catch { succeeded = false; }
        }
        foreach (var className in journal.CreatedClasses.AsEnumerable().Reverse())
        {
            try
            {
                if ((await GetCatalogSnapshotAsync()).Classes.Any(name => string.Equals(
                    name,
                    className,
                    StringComparison.OrdinalIgnoreCase)))
                    await DeleteClassAsync(className);
            }
            catch { succeeded = false; }
        }
        return succeeded;
    }

    private void RecoverInterruptedIdvmImports()
    {
        if (!Directory.Exists(_rootDirectory))
            return;
        foreach (var journalPath in Directory.EnumerateFiles(
            _rootDirectory,
            ".idvm-import-*.json",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<IdvmImportJournal>(
                    File.ReadAllBytes(journalPath),
                    SerializerOptions);
                if (journal is null)
                    continue;
                if (journal.Completed)
                {
                    File.Delete(journalPath);
                    continue;
                }

                var processStart = new DateTimeOffset(
                    System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());
                if (journal.ProcessId == Environment.ProcessId
                    && journal.StartedAtUtc >= processStart.AddSeconds(-5))
                {
                    continue;
                }

                if (File.Exists(CatalogPath))
                {
                    var catalog = JsonSerializer.Deserialize<MapCatalogDocument>(
                        File.ReadAllBytes(CatalogPath),
                        SerializerOptions) ?? new MapCatalogDocument();
                    var importedIds = journal.ImportedMapIds.ToHashSet();
                    catalog.Maps.RemoveAll(map => importedIds.Contains(map.Id));
                    catalog.Classes.RemoveAll(name => journal.CreatedClasses.Contains(
                        name,
                        StringComparer.OrdinalIgnoreCase));
                    if (catalog.Classes.Count == 0)
                        catalog.Classes.Add("S1");
                    var temporaryPath = $"{CatalogPath}.recovery-{Guid.NewGuid():N}";
                    File.WriteAllBytes(
                        temporaryPath,
                        JsonSerializer.SerializeToUtf8Bytes(catalog, SerializerOptions));
                    File.Move(temporaryPath, CatalogPath, overwrite: true);
                }

                foreach (var mapId in journal.ImportedMapIds)
                {
                    var directory = GetMapDirectory(mapId);
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
                File.Delete(journalPath);
            }
            catch
            {
                // Keep an unreadable journal for diagnostics rather than risking
                // deletion of data whose transaction membership is unknown.
            }
        }
    }

    private static void WriteImportJournal(string path, IdvmImportJournal journal)
    {
        var temporaryPath = $"{path}.tmp";
        File.WriteAllBytes(
            temporaryPath,
            JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class IdvmImportJournal
    {
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool Completed { get; set; }
        public List<string> CreatedClasses { get; set; } = [];
        public List<Guid> ImportedMapIds { get; set; } = [];
    }

    private static void ValidateDraft(MapDraft draft)
    {
        // V6: validate at least one floor has a valid image
        var validFloorPaths = draft.FloorPaths
            .Where(kvp => IsSupportedImage(kvp.Value) && File.Exists(kvp.Value))
            .ToList();
        // Fallback to legacy FloorOnePath/FloorTwoPath if FloorPaths is empty
        if (validFloorPaths.Count == 0)
        {
            if (IsSupportedImage(draft.FloorOnePath) && File.Exists(draft.FloorOnePath))
                validFloorPaths.Add(new KeyValuePair<string, string>("1f", draft.FloorOnePath!));
            if (IsSupportedImage(draft.FloorTwoPath) && File.Exists(draft.FloorTwoPath))
                validFloorPaths.Add(new KeyValuePair<string, string>("2f", draft.FloorTwoPath!));
        }

        if (validFloorPaths.Count == 0)
            throw new InvalidOperationException("请至少选择一个有效的地图图片（PNG、JPG 或 JPEG）。");

        if (draft.Floors.Count > 0)
        {
            var orderedFloors = draft.Floors
                .OrderBy(floor => floor.SortOrder)
                .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                .ToArray();
            for (var index = 0; index < orderedFloors.Length; index++)
            {
                var floor = orderedFloors[index];
                EnsureSafeFloorKey(floor.Key);
                var path = draft.FloorPaths.TryGetValue(floor.Key, out var floorPath)
                    ? floorPath
                    : index switch
                    {
                        0 => draft.FloorOnePath,
                        1 => draft.FloorTwoPath,
                        _ => null
                    };
                if (!IsSupportedImage(path) || !File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"Floor '{floor.Key}' is missing a valid source image. Select it again before saving.");
                }

                ValidateReadableImage(path!, $"Floor '{floor.Key}'");
            }
        }
        else
        {
            foreach (var (_, path) in validFloorPaths)
                ValidateReadableImage(path, "Map source image");
        }

        draft.Recognition.EnsureStandardAnchors();
        var primaryFloorKey = draft.Floors
            .OrderBy(floor => floor.SortOrder)
            .ThenBy(floor => floor.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key
            ?? draft.Recognition.FirstFloor.FloorKey;
        if (!draft.Recognition.HasGateMarkers(primaryFloorKey)
            && !(string.Equals(
                    primaryFloorKey,
                    draft.Recognition.FirstFloor.FloorKey,
                    StringComparison.Ordinal)
                && draft.Recognition.HasFirstFloorGateMarkers()))
            throw new InvalidOperationException("请先完成第一张图片的大门和侧门标记。");
    }
}
