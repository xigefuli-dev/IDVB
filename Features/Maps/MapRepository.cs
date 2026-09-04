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
    private const int CurrentStorageSchemaVersion = 18;
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
    private readonly Lazy<SideEntranceFeaturePreprocessor> _sideEntrancePreprocessor =
        new(() => new SideEntranceFeaturePreprocessor());

    public MapRepository(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "Maps");
        RecoverInterruptedIdvmImports();
        RecoverRetiredSubscriptionMaps();
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
                .Select(record => CloneWithClassProperties(catalog, record))
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<MapDraft?> CreateDraftAsync(Guid id) => CreateDraftCoreAsync(id);

    private async Task<MapDraft?> CreateDraftCoreAsync(Guid id, bool gateAlreadyHeld = false)
    {
        if (!gateAlreadyHeld)
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
                    SortOrder = f.SortOrder,
                    MarkerKeys = MapFloorMarkerRules.Normalize(f.MarkerKeys).ToList(),
                    PrebuiltStructureLine = f.PrebuiltStructureLine?.Clone()
                }).ToList(),
                Class = record.Class,
                ClassProperties = GetClassProperties(catalog, record.Class),
                Title = record.Title,
                ContentVersion = record.ContentVersion,
                Source = record.Source,
                AcquisitionKind = record.AcquisitionKind,
                SubscriptionId = record.SubscriptionId,
                SubscriptionPublisherHandle = record.SubscriptionPublisherHandle,
                SubscriptionPublisherIsOfficial = record.SubscriptionPublisherIsOfficial,
                SubscriptionPublisherIsBuilder = record.SubscriptionPublisherIsBuilder,
                SubscriptionPublisherKeyId = record.SubscriptionPublisherKeyId,
                SubscriptionVersion = record.SubscriptionVersion,
                SourceProjectId = record.SourceProjectId,
                SourceProjectRevision = record.SourceProjectRevision,
                SourceVisualSha256 = record.SourceVisualSha256,
                SourceStructureSha256 = record.SourceStructureSha256,
                PortableGates = record.PortableGates.Select(gate => gate.Clone()).ToList(),
                Tags = new Dictionary<Guid, string>(record.Tags),
                Recognition = record.Recognition.Clone(),
                PrebuiltStructureLinePaths = record.Floors
                    .Where(floor => floor.PrebuiltStructureLine?.IsComplete is true)
                    .ToDictionary(
                        floor => floor.Key,
                        floor => GetPrebuiltStructureLinePath(record, floor.Key),
                        StringComparer.Ordinal),
                PrebuiltStructureAlgorithmPath = record.Floors
                    .FirstOrDefault(floor => floor.PrebuiltStructureLine?.IsComplete is true)
                    is { } algorithmFloor
                        ? GetPrebuiltStructureAlgorithmPath(record, algorithmFloor.Key)
                        : null
            };
        }
        finally
        {
            if (!gateAlreadyHeld)
                Gate.Release();
        }
    }

    public Task<MapRecord> SaveAsync(MapDraft draft) =>
        Task.Run(() => SaveCoreAsync(draft));

    private async Task<MapRecord> SaveCoreAsync(
        MapDraft draft,
        bool gateAlreadyHeld = false)
    {
        ValidateDraft(draft);

        if (!gateAlreadyHeld)
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
            record.AcquisitionKind = draft.AcquisitionKind;
            record.SubscriptionId = draft.SubscriptionId;
            record.SubscriptionPublisherHandle = draft.SubscriptionPublisherHandle;
            record.SubscriptionPublisherIsOfficial = draft.SubscriptionPublisherIsOfficial;
            record.SubscriptionPublisherIsBuilder = draft.SubscriptionPublisherIsBuilder;
            record.SubscriptionPublisherKeyId = draft.SubscriptionPublisherKeyId;
            record.SubscriptionVersion = draft.SubscriptionVersion;
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
            record.Tags = (draft.Tags ?? [])
                .Where(pair => pair.Key != Guid.Empty && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
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
                        SortOrder = index + 1,
                        MarkerKeys = MapFloorMarkerRules.Normalize(floor.MarkerKeys).ToList(),
                        PrebuiltStructureLine = floor.PrebuiltStructureLine?.Clone()
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
                var classProperties = GetClassProperties(catalog, record.Class);
                var removeBackground = draft.RemoveBackgroundOverride
                    ?? classProperties.RemoveBackground;
                var backgroundRemovalIntensity = draft.BackgroundRemovalIntensityOverride
                    ?? classProperties.BackgroundRemovalIntensity;
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
                        removeBackground,
                        backgroundRemovalIntensity);
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
                        removeBackground,
                        backgroundRemovalIntensity);
                }
                await PopulateDerivedImageMetadataAsync(
                    floor,
                    sourcePath,
                    recognitionPath,
                    overlayPath,
                    profile,
                    forceRecognitionPath: hasSurveyStructure || needsIndependentRecognition);
                await ImportPrebuiltStructureLineAsync(stagingDirectory, floor, key, draft);

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
                        stagingDirectory, profile);
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
                            removeBackground,
                            backgroundRemovalIntensity);
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
            if (!gateAlreadyHeld)
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
}
