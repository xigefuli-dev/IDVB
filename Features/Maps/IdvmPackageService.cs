using OpenCvSharp;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public enum IdvmExportScope
{
    CurrentClass,
    AllClasses
}

public sealed record IdvmImportResult(
    Guid PackageId,
    IReadOnlyList<string> CreatedClasses,
    IReadOnlyList<MapRecord> ImportedMaps);

public sealed class IdvmImportPlan : IAsyncDisposable
{
    private bool _disposed;

    internal IdvmImportPlan(
        Guid packageId,
        string stagingDirectory,
        IReadOnlyList<MapImportClassDraft> classes)
    {
        PackageId = packageId;
        StagingDirectory = stagingDirectory;
        Classes = classes;
    }

    public Guid PackageId { get; }
    public int ClassCount => Classes.Count;
    public int MapCount => Classes.Sum(item => item.Maps.Count);
    internal string StagingDirectory { get; }
    internal IReadOnlyList<MapImportClassDraft> Classes { get; }
    internal bool IsDisposed => _disposed;

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        try
        {
            if (Directory.Exists(StagingDirectory))
                Directory.Delete(StagingDirectory, recursive: true);
        }
        catch
        {
            // A later temp cleanup may remove files held by an image decoder.
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Reads and writes the portable, untrusted IDVM interchange format.</summary>
public sealed class IdvmPackageService
{
    private const int HeaderSize = 80;
    private const int MaxEntries = 4096;
    private const long MaxSingleFileBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const double CoordinateTolerance = 0.000001d;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 64,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MapRepository _repository;

    public IdvmPackageService(MapRepository repository)
    {
        _repository = repository;
    }

    public async Task ExportAsync(
        IdvmExportScope scope,
        string? className,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("导出目标不能为空。", nameof(destination));

        var snapshot = await _repository.GetCatalogSnapshotAsync();
        var selectedMaps = scope == IdvmExportScope.CurrentClass
            ? snapshot.Maps.Where(map => string.Equals(
                map.Class,
                className,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : snapshot.Maps.ToArray();
        if (selectedMaps.Length == 0)
            throw new InvalidOperationException("所选范围没有可导出的地图。");

        var selectedClasses = snapshot.Classes
            .Where(name => selectedMaps.Any(map => string.Equals(
                map.Class,
                name,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var staging = CreateTemporaryDirectory("export");
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryDestination = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var packageId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var classIds = selectedClasses.ToDictionary(
                name => name,
                _ => Guid.NewGuid(),
                StringComparer.OrdinalIgnoreCase);
            var manifest = new ManifestDto
            {
                Format = "idvm",
                FormatVersion = "1.0",
                PackageType = "class-set",
                PackageId = packageId,
                CreatedAt = createdAt,
                MinimumReader = "1.0",
                Capabilities = new CapabilitiesDto()
            };

            foreach (var classLabel in selectedClasses)
            {
                var maps = selectedMaps
                    .Where(map => string.Equals(map.Class, classLabel, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(map => map.SequenceNumber)
                    .ToArray();
                var classId = classIds[classLabel];
                manifest.Classes.Add(new ManifestClassDto
                {
                    ClassId = classId,
                    Name = classLabel,
                    MapIds = maps.Select(map => map.Id).ToList()
                });
                foreach (var map in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    manifest.Maps.Add(await WriteMapPayloadAsync(
                        staging,
                        classId,
                        map,
                        cancellationToken));
                }
            }

            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = ToLogicalPath(staging, file);
                manifest.Files.Add(new ManifestFileDto
                {
                    Path = relative,
                    Size = new FileInfo(file).Length,
                    Sha256 = await ComputeSha256Async(file, cancellationToken)
                });
            }

            var manifestBytes = SerializeUtf8(manifest);
            var manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            var header = CreateHeader(packageId, createdAt, SHA256.HashData(manifestBytes));
            await File.WriteAllBytesAsync(Path.Combine(staging, "header"), header, cancellationToken);

            await CreateArchiveAsync(staging, temporaryDestination, cancellationToken);
            File.Move(temporaryDestination, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryDestination))
                File.Delete(temporaryDestination);
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<IdvmImportPlan> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath)
            || (!File.Exists(packagePath) && !Directory.Exists(packagePath)))
        {
            throw new FileNotFoundException("找不到要导入的 IDVM 数据包。", packagePath);
        }

        var staging = CreateTemporaryDirectory("inspect");
        try
        {
            await ExtractPackageAsync(packagePath, staging, cancellationToken);
            var manifest = await ValidatePackageAsync(staging, cancellationToken);
            var classes = await BuildImportDraftsAsync(staging, manifest, cancellationToken);
            return new IdvmImportPlan(manifest.PackageId, staging, classes);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public async Task<IdvmImportResult> ImportAsync(
        IdvmImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsDisposed || !Directory.Exists(plan.StagingDirectory))
            throw new InvalidOperationException("IDVM 导入计划已失效，请重新选择数据包。");
        try
        {
            var result = await _repository.ImportBatchAsync(plan.Classes, cancellationToken);
            return new IdvmImportResult(plan.PackageId, result.CreatedClasses, result.ImportedMaps);
        }
        finally
        {
            await plan.DisposeAsync();
        }
    }

    private async Task<ManifestMapDto> WriteMapPayloadAsync(
        string staging,
        Guid classId,
        MapRecord map,
        CancellationToken cancellationToken)
    {
        var root = $"maps/{map.Id:N}";
        var mapDirectory = Path.Combine(staging, root.Replace('/', Path.DirectorySeparatorChar));
        var imageDirectory = Path.Combine(mapDirectory, "maps");
        var dataDirectory = Path.Combine(mapDirectory, "data");
        Directory.CreateDirectory(imageDirectory);
        Directory.CreateDirectory(dataDirectory);

        var manifestMap = new ManifestMapDto
        {
            MapId = map.Id,
            MapVersion = Math.Max(1, map.ContentVersion),
            Name = map.DisplayName,
            ClassId = classId,
            CreatedAt = map.CreatedAt,
            UpdatedAt = map.UpdatedAt,
            Root = root
        };
        var metadata = new MetadataDto
        {
            Map = new MetadataMapDto
            {
                Id = map.Id,
                ClassId = classId,
                Title = map.DisplayName,
                Source = "manual",
                CoordinateSystem = "normalized-top-left-y-down"
            },
            Recognition = new RecognitionSettingsDto
            {
                WholeImage = new WholeImageDto
                {
                    Enabled = map.Recognition.WholeImage.IsEnabled,
                    Weight = map.Recognition.WholeImage.Weight,
                    AnnotatedReferencePenalty = map.Recognition.WholeImage.AnnotatedReferencePenalty,
                    ReferenceMayContainAnnotations = map.Recognition.WholeImage.ReferenceMayContainAnnotations
                }
            }
        };

        var orderedFloors = MapFloorRules.GetOrderedFloors(map);
        for (var index = 0; index < orderedFloors.Count; index++)
        {
            var floor = orderedFloors[index];
            var source = _repository.GetFloorImagePath(map, floor.Key);
            if (!File.Exists(source))
                throw new InvalidOperationException($"{map.DisplayName} 的楼层“{floor.DisplayName}”原图不存在。");
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!MapRepository.IsSupportedImage(source))
                throw new InvalidOperationException($"{map.DisplayName} 包含不支持的图片格式。");
            var imageLogicalPath = $"{root}/maps/floor-{index + 1:D3}{extension}";
            var target = Path.Combine(staging, imageLogicalPath.Replace('/', Path.DirectorySeparatorChar));
            await CopyFileAsync(source, target, cancellationToken);
            using var image = Cv2.ImRead(target, ImreadModes.Unchanged);
            if (image.Empty())
                throw new InvalidOperationException($"无法读取 {map.DisplayName} 的楼层原图。");

            var profile = map.Recognition.GetFloor(floor.Key)
                ?? throw new InvalidOperationException($"{map.DisplayName} 缺少楼层 {floor.Key} 的识别配置。");
            var manifestFloor = new ManifestFloorDto
            {
                Key = floor.Key,
                DisplayName = floor.DisplayName,
                SortOrder = index + 1,
                Image = imageLogicalPath
            };
            manifestMap.Floors.Add(manifestFloor);
            metadata.Floors.Add(new MetadataFloorDto
            {
                Key = floor.Key,
                DisplayName = floor.DisplayName,
                SortOrder = index + 1,
                Image = imageLogicalPath,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                OrientationDegrees = profile.OrientationDegrees,
                RecognitionRegion = ToDto(profile.RecognitionRegion),
                ValidMapBounds = NormalizeBounds(profile.ValidMapBounds,
                    profile.RecognitionPixelWidth,
                    profile.RecognitionPixelHeight),
                SideEntranceFeature = await TryExportSideEntranceFeatureAsync(
                    staging, root, index + 1, map, floor.Key, profile, cancellationToken)
            });
        }

        var gates = BuildPortableGates(map);
        var gatesDocument = new GatesDto
        {
            Gates = gates.Select(gate => new GateDto
            {
                Id = gate.Id,
                FloorKey = gate.FloorKey,
                Role = gate.Role,
                Bounds = ToDto(gate.Bounds)!,
                DirectionDegrees = gate.DirectionDegrees,
                Enabled = gate.Enabled,
                Confidence = gate.Confidence
            }).ToList()
        };
        var anchorsDocument = new AnchorsDto();
        foreach (var floor in orderedFloors)
        {
            var profile = map.Recognition.GetFloor(floor.Key)!;
            var floorAnchors = new AnchorFloorDto
            {
                WholeImageIgnoreRegions = profile.WholeImageIgnoreRegions
                    .Select(region => ToDto(region)!).ToList(),
                Annotations = profile.Annotations.Select(annotation => new AnnotationDto
                {
                    Id = annotation.Id,
                    Type = annotation.Type == MapAnnotationType.Text ? "text" : "outline",
                    ColorIndex = annotation.ColorIndex,
                    Bounds = ToDto(annotation.Bounds)!,
                    Text = annotation.Text
                }).ToList()
            };
            foreach (var anchor in profile.Anchors)
            {
                var gate = IsGateAnchor(anchor.Key)
                    ? gates.FirstOrDefault(item => string.Equals(item.FloorKey, floor.Key, StringComparison.Ordinal)
                        && string.Equals(item.Role, GateRoleForAnchor(anchor.Key), StringComparison.Ordinal))
                    : null;
                floorAnchors.Anchors.Add(new AnchorDto
                {
                    Id = anchor.Id,
                    Key = anchor.Key,
                    DisplayName = anchor.DisplayName,
                    Role = anchor.Role == RecognitionAnchorRole.Required ? "required" : "optional",
                    Weight = anchor.Weight,
                    BuiltIn = anchor.IsBuiltIn,
                    GateId = gate?.Id,
                    Bounds = gate is null ? ToDto(anchor.Bounds) : null
                });
            }
            anchorsDocument.Floors.Add(floor.Key, floorAnchors);
        }

        await WriteJsonAsync(Path.Combine(dataDirectory, "metadata.json"), metadata, cancellationToken);
        await WriteJsonAsync(Path.Combine(dataDirectory, "gates.json"), gatesDocument, cancellationToken);
        await WriteJsonAsync(Path.Combine(dataDirectory, "anchors.json"), anchorsDocument, cancellationToken);
        return manifestMap;
    }

    /// <summary>
    /// 若地图该楼层已有有效侧门特征图，将其复制到 staging 并返回 DTO；否则返回 null。
    /// </summary>
    private async Task<SideEntranceFeatureDto?> TryExportSideEntranceFeatureAsync(
        string staging,
        string root,
        int floorIndex,
        MapRecord map,
        string floorKey,
        FloorRecognitionProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName)
            || profile.SideEntranceFeatureRadius <= 0)
            return null;

        var sourcePath = _repository.GetSideEntranceFeaturePath(map, floorKey);
        if (!File.Exists(sourcePath))
            return null;

        var featureLogicalPath =
            $"{root}/data/floor-{floorIndex:D3}-side-entrance-feature.png";
        var featureTarget = Path.Combine(
            staging,
            featureLogicalPath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            await CopyFileAsync(sourcePath, featureTarget, cancellationToken);
            return new SideEntranceFeatureDto
            {
                File    = featureLogicalPath,
                CenterX = profile.SideEntranceFeatureCenterX,
                CenterY = profile.SideEntranceFeatureCenterY,
                Radius  = profile.SideEntranceFeatureRadius
            };
        }
        catch
        {
            // 特征图复制失败不阻断主导出流程
            return null;
        }
    }

    private static List<MapGateDefinition> BuildPortableGates(MapRecord map)
    {
        var gates = map.PortableGates
            .Where(IsValidPortableGate)
            .Select(item => item.Clone())
            .ToList();
        foreach (var floor in MapFloorRules.GetOrderedFloors(map))
        {
            var profile = map.Recognition.GetFloor(floor.Key);
            if (profile is null)
                continue;
            foreach (var (anchorKey, role) in new[]
            {
                ("main-entrance", "mainEntrance"),
                ("side-entrance", "sideEntrance")
            })
            {
                var anchor = profile.FindAnchor(anchorKey);
                if (anchor?.Bounds?.IsValid is not true)
                    continue;
                var gate = gates.FirstOrDefault(item =>
                    string.Equals(item.FloorKey, floor.Key, StringComparison.Ordinal)
                    && string.Equals(item.Role, role, StringComparison.Ordinal));
                if (gate is null)
                {
                    gate = new MapGateDefinition
                    {
                        Id = $"{floor.Key}-{(role == "mainEntrance" ? "main" : "side")}",
                        FloorKey = floor.Key,
                        Role = role
                    };
                    gates.Add(gate);
                }
                gate.Bounds = anchor.Bounds.Clone();
            }
        }
        return gates;
    }

    private static async Task<ManifestDto> ValidatePackageAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var headerPath = Path.Combine(root, "header");
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(headerPath) || !File.Exists(manifestPath))
            throw new InvalidDataException("IDVM 包缺少 header 或 manifest.json。");
        var header = await File.ReadAllBytesAsync(headerPath, cancellationToken);
        if (header.Length != HeaderSize)
            throw new InvalidDataException("IDVM header 长度必须为 80 字节。");
        var parsedHeader = ParseHeader(header);
        var manifestBytes = await ReadLimitedBytesAsync(manifestPath, MaxJsonBytes, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
            parsedHeader.ManifestSha256,
            SHA256.HashData(manifestBytes)))
        {
            throw new InvalidDataException("manifest.json 的 SHA-256 与 header 不一致。");
        }

        var manifest = Deserialize<ManifestDto>(manifestBytes, "manifest.json");
        if (manifest.Classes is null || manifest.Maps is null || manifest.Files is null)
            throw new InvalidDataException("manifest 的 classes、maps 和 files 必须是数组。");
        if (manifest.Format != "idvm" || manifest.FormatVersion != "1.0"
            || manifest.MinimumReader != "1.0" || manifest.PackageType != "class-set")
        {
            throw new InvalidDataException("不支持的 IDVM 格式或读取器版本。");
        }
        if (manifest.PackageId == Guid.Empty || manifest.PackageId != parsedHeader.PackageId)
            throw new InvalidDataException("header 与 manifest 的 packageId 不一致。");
        if (manifest.CreatedAt.ToUnixTimeMilliseconds() != parsedHeader.CreatedAtUnixMilliseconds)
            throw new InvalidDataException("header 与 manifest 的创建时间不一致。");
        if (manifest.Classes.Count is < 1 or > 256 || manifest.Maps.Count is < 1 or > 4096)
            throw new InvalidDataException("IDVM 包的 Class 或地图数量超出限制。");

        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => ToLogicalPath(root, path))
            .Where(path => path is not "header" and not "manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            ValidateLogicalPath(file.Path);
            if (!file.Path.StartsWith("maps/", StringComparison.Ordinal)
                || !declaredPaths.Add(file.Path))
                throw new InvalidDataException($"manifest 包含重复或未知路径：{file.Path}");
            var path = ToPhysicalPath(root, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
                throw new InvalidDataException($"文件大小不匹配：{file.Path}");
            var hash = await ComputeSha256Async(path, cancellationToken);
            if (!IsSha256(file.Sha256)
                || !string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"文件摘要不匹配：{file.Path}");
        }
        if (!actualFiles.SetEquals(declaredPaths))
            throw new InvalidDataException("manifest 文件清单与包内容不一致。");

        ValidateManifestRelationships(manifest);
        return manifest;
    }

    private static void ValidateManifestRelationships(ManifestDto manifest)
    {
        var classIds = new HashSet<Guid>();
        var classNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapIds = new HashSet<Guid>();
        foreach (var item in manifest.Classes)
        {
            if (item is null || item.MapIds is null
                || item.ClassId == Guid.Empty || !classIds.Add(item.ClassId)
                || string.IsNullOrWhiteSpace(item.Name) || !classNames.Add(item.Name.Trim())
                || item.MapIds.Count == 0 || item.MapIds.Count != item.MapIds.Distinct().Count())
            {
                throw new InvalidDataException("manifest 包含无效或重复的 Class。");
            }
        }
        foreach (var map in manifest.Maps)
        {
            if (map is null || map.Floors is null
                || map.MapId == Guid.Empty || !mapIds.Add(map.MapId) || map.MapVersion <= 0
                || !classIds.Contains(map.ClassId) || string.IsNullOrWhiteSpace(map.Name))
                throw new InvalidDataException("manifest 包含无效或重复的地图。");
            var expectedRoot = $"maps/{map.MapId:N}";
            if (!string.Equals(map.Root, expectedRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"地图 {map.MapId} 的资源根路径无效。");
            ValidateFloors(map.Floors, expectedRoot);
        }
        foreach (var item in manifest.Classes)
        {
            var expected = manifest.Maps.Where(map => map.ClassId == item.ClassId)
                .Select(map => map.MapId).ToHashSet();
            if (!expected.SetEquals(item.MapIds))
                throw new InvalidDataException($"Class “{item.Name}”的地图索引不一致。");
        }
        if (manifest.Classes.SelectMany(item => item.MapIds).Count() != manifest.Maps.Count)
            throw new InvalidDataException("地图必须且只能属于一个 Class。");
    }

    private static void ValidateFloors(IReadOnlyList<ManifestFloorDto> floors, string root)
    {
        if (floors.Count == 0)
            throw new InvalidDataException("地图必须至少包含一个楼层。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < floors.Count; index++)
        {
            var floor = floors[index];
            if (!IsSafeIdentifier(floor.Key) || !keys.Add(floor.Key)
                || string.IsNullOrWhiteSpace(floor.DisplayName) || floor.SortOrder != index + 1)
                throw new InvalidDataException("地图包含无效的楼层 ID、名称或顺序。");
            ValidateLogicalPath(floor.Image);
            if (!floor.Image.StartsWith($"{root}/maps/", StringComparison.Ordinal)
                || !MapRepository.IsSupportedImage(floor.Image))
                throw new InvalidDataException($"楼层 {floor.Key} 的图片路径无效。");
        }
    }

    private static async Task<IReadOnlyList<MapImportClassDraft>> BuildImportDraftsAsync(
        string root,
        ManifestDto manifest,
        CancellationToken cancellationToken)
    {
        var draftsByClass = new Dictionary<Guid, List<MapDraft>>();
        foreach (var item in manifest.Classes)
            draftsByClass[item.ClassId] = [];

        foreach (var map in manifest.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataRoot = Path.Combine(root, map.Root.Replace('/', Path.DirectorySeparatorChar), "data");
            var metadata = await ReadJsonAsync<MetadataDto>(Path.Combine(dataRoot, "metadata.json"), cancellationToken);
            var gatesDocument = await ReadJsonAsync<GatesDto>(Path.Combine(dataRoot, "gates.json"), cancellationToken);
            var anchorsDocument = await ReadJsonAsync<AnchorsDto>(Path.Combine(dataRoot, "anchors.json"), cancellationToken);
            ValidateMapDocuments(map, metadata, gatesDocument, anchorsDocument);

            var floorDefinitions = new List<FloorDefinition>();
            var floorPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var profiles = new Dictionary<string, FloorRecognitionProfile>(StringComparer.Ordinal);
            var gates = gatesDocument.Gates.Select(ToModel).ToList();
            var sideEntranceFeaturePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < metadata.Floors.Count; index++)
            {
                var floor = metadata.Floors[index];
                var imagePath = ToPhysicalPath(root, floor.Image);
                using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
                if (image.Empty() || image.Width != floor.ImageWidth || image.Height != floor.ImageHeight)
                    throw new InvalidDataException($"地图“{map.Name}”楼层 {floor.Key} 的图片尺寸不一致。");
                var recognitionSize = GetRecognitionSize(floor, image.Width, image.Height);
                var anchorFloor = anchorsDocument.Floors[floor.Key];
                var profile = new FloorRecognitionProfile
                {
                    Floor = index == 0 ? MapFloor.First : MapFloor.Second,
                    FloorKey = floor.Key,
                    OrientationDegrees = floor.OrientationDegrees,
                    RecognitionRegion = ToModel(floor.RecognitionRegion),
                    RecognitionPixelWidth = recognitionSize.Width,
                    RecognitionPixelHeight = recognitionSize.Height,
                    ValidMapBounds = ToPixelBounds(
                        floor.ValidMapBounds,
                        recognitionSize.Width,
                        recognitionSize.Height),
                    WholeImageIgnoreRegions = anchorFloor.WholeImageIgnoreRegions.Select(ToRequiredModel).ToList(),
                    Annotations = anchorFloor.Annotations.Select(ToModel).ToList()
                };
                foreach (var anchor in anchorFloor.Anchors)
                {
                    NormalizedRectangle? bounds;
                    if (!string.IsNullOrWhiteSpace(anchor.GateId))
                    {
                        var gate = gates.SingleOrDefault(item => string.Equals(
                            item.Id,
                            anchor.GateId,
                            StringComparison.Ordinal));
                        if (gate is null || anchor.Bounds is not null)
                            throw new InvalidDataException($"锚点 {anchor.Key} 的 gateId 无效或重复声明 bounds。");
                        bounds = gate.Bounds.Clone();
                    }
                    else
                    {
                        bounds = ToModel(anchor.Bounds);
                    }
                    profile.Anchors.Add(new RecognitionAnchor
                    {
                        Id = anchor.Id == Guid.Empty ? Guid.NewGuid() : anchor.Id,
                        Key = anchor.Key,
                        DisplayName = anchor.DisplayName,
                        Role = anchor.Role == "required" ? RecognitionAnchorRole.Required : RecognitionAnchorRole.Optional,
                        Weight = anchor.Weight,
                        Bounds = bounds,
                        IsBuiltIn = anchor.BuiltIn
                    });
                }
                floorDefinitions.Add(new FloorDefinition
                {
                    Key = floor.Key,
                    DisplayName = floor.DisplayName,
                    SortOrder = floor.SortOrder
                });
                floorPaths.Add(floor.Key, imagePath);
                profiles.Add(floor.Key, profile);

                // 导入侧门特征图（可选，旧包无此字段时静默跳过）
                if (floor.SideEntranceFeature is { } featureDto
                    && !string.IsNullOrWhiteSpace(featureDto.File)
                    && featureDto.Radius > 0)
                {
                    var featurePhysical = ToPhysicalPath(root, featureDto.File);
                    if (File.Exists(featurePhysical))
                    {
                        profile.SideEntranceFeatureCenterX = featureDto.CenterX;
                        profile.SideEntranceFeatureCenterY = featureDto.CenterY;
                        profile.SideEntranceFeatureRadius   = featureDto.Radius;
                        sideEntranceFeaturePaths[floor.Key] = featurePhysical;
                    }
                }
            }

            var orderedProfiles = metadata.Floors.Select(floor => profiles[floor.Key]).ToArray();
            var recognition = new MapRecognitionProfile
            {
                SchemaVersion = 6,
                FirstFloor = orderedProfiles[0],
                SecondFloor = orderedProfiles.Length > 1
                    ? orderedProfiles[1]
                    : new FloorRecognitionProfile { Floor = MapFloor.Second, FloorKey = "2f" },
                Floors = profiles,
                WholeImage = new WholeImageRecognitionSettings
                {
                    IsEnabled = metadata.Recognition.WholeImage.Enabled,
                    Weight = metadata.Recognition.WholeImage.Weight,
                    AnnotatedReferencePenalty = metadata.Recognition.WholeImage.AnnotatedReferencePenalty,
                    ReferenceMayContainAnnotations = metadata.Recognition.WholeImage.ReferenceMayContainAnnotations
                }
            };
            recognition.EnsureStandardAnchors();
            var draft = new MapDraft
            {
                Title = metadata.Map.Title,
                ContentVersion = map.MapVersion,
                Floors = floorDefinitions,
                FloorPaths = floorPaths,
                FloorPreviewPaths = new Dictionary<string, string>(floorPaths, StringComparer.Ordinal),
                FloorOnePath = floorPaths[metadata.Floors[0].Key],
                FloorTwoPath = metadata.Floors.Count > 1 ? floorPaths[metadata.Floors[1].Key] : null,
                Recognition = recognition,
                PortableGates = gates,
                SideEntranceFeaturePaths = sideEntranceFeaturePaths
            };
            draftsByClass[map.ClassId].Add(draft);
        }

        return manifest.Classes.Select(item => new MapImportClassDraft(
            item.Name.Trim(),
            draftsByClass[item.ClassId])).ToArray();
    }

    private static void ValidateMapDocuments(
        ManifestMapDto map,
        MetadataDto metadata,
        GatesDto gates,
        AnchorsDto anchors)
    {
        if (metadata.Map is null || metadata.Floors is null || metadata.Recognition?.WholeImage is null
            || gates.Gates is null || anchors.Floors is null)
            throw new InvalidDataException("地图数据文件缺少必需对象或数组。");
        if (metadata.SchemaVersion != 1 || gates.SchemaVersion != 1 || anchors.SchemaVersion != 1)
            throw new InvalidDataException("不支持的数据 schemaVersion。");
        if (metadata.Map.Id != map.MapId || metadata.Map.ClassId != map.ClassId
            || metadata.Map.CoordinateSystem != "normalized-top-left-y-down"
            || string.IsNullOrWhiteSpace(metadata.Map.Title))
            throw new InvalidDataException($"地图 {map.MapId} 的 metadata 标识不一致。");
        if (metadata.Floors.Count != map.Floors.Count)
            throw new InvalidDataException($"地图 {map.MapId} 的楼层清单不一致。");
        for (var index = 0; index < map.Floors.Count; index++)
        {
            var declared = map.Floors[index];
            var floor = metadata.Floors[index];
            if (floor.Key != declared.Key || floor.DisplayName != declared.DisplayName
                || floor.SortOrder != declared.SortOrder || floor.Image != declared.Image
                || floor.ImageWidth <= 0 || floor.ImageHeight <= 0
                || floor.OrientationDegrees is not (0 or 90 or 180 or 270)
                || !anchors.Floors.ContainsKey(floor.Key))
            {
                throw new InvalidDataException($"地图 {map.MapId} 的楼层 metadata 无效。");
            }
            ValidateRectangle(floor.RecognitionRegion, allowNull: true, "recognitionRegion");
            ValidateRectangle(floor.ValidMapBounds, allowNull: false, "validMapBounds");
            var anchorFloor = anchors.Floors[floor.Key];
            foreach (var region in anchorFloor.WholeImageIgnoreRegions)
                ValidateRectangle(region, allowNull: false, "wholeImageIgnoreRegions");
            var anchorIds = new HashSet<Guid>();
            var anchorKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var anchor in anchorFloor.Anchors)
            {
                if (anchor.Id == Guid.Empty || !anchorIds.Add(anchor.Id)
                    || !IsSafeIdentifier(anchor.Key) || !anchorKeys.Add(anchor.Key)
                    || string.IsNullOrWhiteSpace(anchor.DisplayName)
                    || anchor.Role is not ("required" or "optional")
                    || !double.IsFinite(anchor.Weight) || anchor.Weight < 0d
                    || (anchor.Role == "required"
                        && string.IsNullOrWhiteSpace(anchor.GateId)
                        && anchor.Bounds is null))
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效锚点。");
                ValidateRectangle(
                    anchor.Bounds,
                    allowNull: anchor.Role == "optional" || !string.IsNullOrWhiteSpace(anchor.GateId),
                    "anchor.bounds");
            }
            foreach (var annotation in anchorFloor.Annotations)
            {
                if (annotation.Id == Guid.Empty || annotation.Type is not ("text" or "outline")
                    || annotation.ColorIndex is < 0 or > 8)
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效标注。");
                ValidateRectangle(annotation.Bounds, allowNull: false, "annotation.bounds");
            }
        }
        if (anchors.Floors.Count != metadata.Floors.Count)
            throw new InvalidDataException("anchors.json 包含未知楼层。");

        var floorKeys = metadata.Floors.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var gateIds = new HashSet<string>(StringComparer.Ordinal);
        var mainFloors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in gates.Gates)
        {
            if (!IsSafeIdentifier(gate.Id) || !gateIds.Add(gate.Id)
                || !floorKeys.Contains(gate.FloorKey)
                || gate.Role is not ("mainEntrance" or "sideEntrance" or "exit" or "unknown")
                || !double.IsFinite(gate.DirectionDegrees)
                || !double.IsFinite(gate.Confidence) || gate.Confidence is < 0d or > 1d)
                throw new InvalidDataException("gates.json 包含无效门数据。");
            ValidateRectangle(gate.Bounds, allowNull: false, "gate.bounds");
            if (gate.Role == "mainEntrance" && !mainFloors.Add(gate.FloorKey))
                throw new InvalidDataException("同一楼层只能包含一个 mainEntrance。");
        }
        foreach (var anchor in anchors.Floors.Values.SelectMany(item => item.Anchors))
            if (!string.IsNullOrWhiteSpace(anchor.GateId) && !gateIds.Contains(anchor.GateId))
                throw new InvalidDataException($"锚点 {anchor.Key} 引用了不存在的门。");

        var primaryFloorKey = metadata.Floors[0].Key;
        var primaryAnchors = anchors.Floors[primaryFloorKey].Anchors;
        foreach (var (anchorKey, gateRole) in new[]
        {
            ("main-entrance", "mainEntrance"),
            ("side-entrance", "sideEntrance")
        })
        {
            var gate = gates.Gates.SingleOrDefault(item =>
                item.FloorKey == primaryFloorKey && item.Role == gateRole);
            if (gate is null || !primaryAnchors.Any(anchor =>
                    anchor.Key == anchorKey && anchor.GateId == gate.Id))
            {
                throw new InvalidDataException(
                    $"主楼层必须包含通过 gateId 关联的 {anchorKey} 门锚点。");
            }
        }
    }

    private static async Task ExtractPackageAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(source))
        {
            await CopyPackageDirectoryAsync(source, destination, cancellationToken);
            return;
        }

        await using var stream = File.OpenRead(source);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("IDVM 包的 ZIP 条目过多。");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;
            ValidateLogicalPath(entry.FullName);
            if (!paths.Add(entry.FullName))
                throw new InvalidDataException($"ZIP 包含重复路径：{entry.FullName}");
            if (entry.Length > MaxSingleFileBytes || entry.Length < 0)
                throw new InvalidDataException($"ZIP 条目过大：{entry.FullName}");
            total = checked(total + entry.Length);
            if (total > MaxExpandedBytes)
                throw new InvalidDataException("IDVM 包展开后超过 512 MiB。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000))
                throw new InvalidDataException($"ZIP 条目不是普通文件：{entry.FullName}");
            var target = ToPhysicalPath(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyStreamLimitedAsync(input, output, entry.Length, cancellationToken);
        }
    }

    private static async Task CopyPackageDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length > MaxEntries)
            throw new InvalidDataException("IDVM 目录包含过多文件。");
        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("IDVM 目录不得包含符号链接或重解析点。");
            var logical = ToLogicalPath(source, file);
            ValidateLogicalPath(logical);
            var length = new FileInfo(file).Length;
            if (length > MaxSingleFileBytes)
                throw new InvalidDataException($"文件过大：{logical}");
            total = checked(total + length);
            if (total > MaxExpandedBytes)
                throw new InvalidDataException("IDVM 目录总大小超过 512 MiB。");
            await CopyFileAsync(file, ToPhysicalPath(destination, logical), cancellationToken);
        }
    }

    private static async Task CreateArchiveAsync(
        string sourceDirectory,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var relative in new[] { "header", "manifest.json" }.Concat(
            Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => ToLogicalPath(sourceDirectory, path))
                .Where(path => path is not "header" and not "manifest.json")
                .OrderBy(path => path, StringComparer.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(
                relative,
                relative == "header" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            await using var input = File.OpenRead(ToPhysicalPath(sourceDirectory, relative));
            await using var output = entry.Open();
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static byte[] CreateHeader(Guid packageId, DateTimeOffset createdAt, byte[] manifestHash)
    {
        var bytes = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("IDVM").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), HeaderSize);
        WriteRfc4122Guid(packageId, bytes.AsSpan(12, 16));
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(28, 8),
            createdAt.ToUnixTimeMilliseconds());
        manifestHash.CopyTo(bytes, 36);
        return bytes;
    }

    private static ParsedHeader ParseHeader(byte[] bytes)
    {
        if (!bytes.AsSpan(0, 4).SequenceEqual("IDVM"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)) != HeaderSize
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2)) != 0
            || bytes.AsSpan(68, 12).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException("IDVM header 的 magic、版本、flags 或保留字段无效。");
        }
        return new ParsedHeader(
            ReadRfc4122Guid(bytes.AsSpan(12, 16)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28, 8)),
            bytes.AsSpan(36, 32).ToArray());
    }

    private static void WriteRfc4122Guid(Guid value, Span<byte> destination)
    {
        var raw = Convert.FromHexString(value.ToString("N"));
        raw.CopyTo(destination);
    }

    private static Guid ReadRfc4122Guid(ReadOnlySpan<byte> source)
    {
        var hex = Convert.ToHexString(source);
        return Guid.ParseExact(hex, "N");
    }

    private static void ValidateLogicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains('\0')
            || path.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
        {
            throw new InvalidDataException($"IDVM 包含不安全路径：{path}");
        }
    }

    private static string ToPhysicalPath(string root, string logicalPath)
    {
        ValidateLogicalPath(logicalPath);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, logicalPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路径越出 IDVM 根目录：{logicalPath}");
        return fullPath;
    }

    private static string ToLogicalPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string CreateTemporaryDirectory(string purpose)
    {
        var root = Path.Combine(Path.GetTempPath(), "IDVBuff", "IDVM", purpose, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task CopyStreamLimitedAsync(
        Stream input,
        Stream output,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            written += read;
            if (written > expectedLength || written > MaxSingleFileBytes)
                throw new InvalidDataException("ZIP 条目实际展开大小超过声明值。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (written != expectedLength)
            throw new InvalidDataException("ZIP 条目实际大小与声明值不一致。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        string path,
        long maximum,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > maximum)
            throw new InvalidDataException($"JSON 文件超过 {maximum} 字节限制：{Path.GetFileName(path)}");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"IDVM 包缺少 {Path.GetFileName(path)}。");
        return Deserialize<T>(
            await ReadLimitedBytesAsync(path, MaxJsonBytes, cancellationToken),
            Path.GetFileName(path));
    }

    private static T Deserialize<T>(byte[] bytes, string name)
    {
        try
        {
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }))
                ValidateJsonShape(document.RootElement, name);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidDataException($"{name} 不能为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{name} 不是有效的 IDVM JSON。", exception);
        }
    }

    private static void ValidateJsonShape(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"{name} 包含重复字段：{property.Name}");
                ValidateJsonShape(property.Value, name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 100_000)
                throw new InvalidDataException($"{name} 包含过大的 JSON 数组。");
            foreach (var item in element.EnumerateArray())
                ValidateJsonShape(item, name);
        }
    }

    private static byte[] SerializeUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path, SerializeUtf8(value), cancellationToken);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Contains('/')
        && !value.Contains('\\')
        && value is not "." and not "..";

    private static bool IsValidPortableGate(MapGateDefinition gate) =>
        IsSafeIdentifier(gate.Id)
        && IsSafeIdentifier(gate.FloorKey)
        && gate.Role is "mainEntrance" or "sideEntrance" or "exit" or "unknown"
        && gate.Bounds.IsValid
        && double.IsFinite(gate.DirectionDegrees)
        && double.IsFinite(gate.Confidence)
        && gate.Confidence is >= 0d and <= 1d;

    private static bool IsGateAnchor(string key) => key is "main-entrance" or "side-entrance";
    private static string GateRoleForAnchor(string key) => key == "main-entrance" ? "mainEntrance" : "sideEntrance";

    private static RectangleDto? ToDto(NormalizedRectangle? rectangle) => rectangle is null ? null : new RectangleDto
    {
        X = rectangle.X,
        Y = rectangle.Y,
        Width = rectangle.Width,
        Height = rectangle.Height
    };

    private static RectangleDto NormalizeBounds(MapReferenceBounds? bounds, int width, int height)
    {
        if (bounds?.IsValid is not true || width <= 0 || height <= 0)
            return new RectangleDto { Width = 1d, Height = 1d };
        return new RectangleDto
        {
            X = bounds.X / width,
            Y = bounds.Y / height,
            Width = bounds.Width / width,
            Height = bounds.Height / height
        };
    }

    private static NormalizedRectangle? ToModel(RectangleDto? rectangle) => rectangle is null ? null : new NormalizedRectangle
    {
        X = Math.Clamp(rectangle.X, 0d, 1d),
        Y = Math.Clamp(rectangle.Y, 0d, 1d),
        Width = Math.Clamp(rectangle.Width, 0d, 1d),
        Height = Math.Clamp(rectangle.Height, 0d, 1d)
    };

    private static NormalizedRectangle ToRequiredModel(RectangleDto rectangle) => ToModel(rectangle)!;

    private static MapReferenceBounds ToPixelBounds(RectangleDto bounds, int width, int height) => new()
    {
        X = bounds.X * width,
        Y = bounds.Y * height,
        Width = bounds.Width * width,
        Height = bounds.Height * height
    };

    private static Size GetRecognitionSize(MetadataFloorDto floor, int imageWidth, int imageHeight)
    {
        if (floor.RecognitionRegion is null)
            return new Size(imageWidth, imageHeight);
        var region = floor.RecognitionRegion;
        var left = Math.Clamp((int)Math.Floor(region.X * imageWidth), 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(region.Y * imageHeight), 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * imageWidth), left + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * imageHeight), top + 1, imageHeight);
        return new Size(right - left, bottom - top);
    }

    private static MapGateDefinition ToModel(GateDto gate) => new()
    {
        Id = gate.Id,
        FloorKey = gate.FloorKey,
        Role = gate.Role,
        Bounds = ToRequiredModel(gate.Bounds),
        DirectionDegrees = gate.DirectionDegrees,
        Enabled = gate.Enabled,
        Confidence = gate.Confidence
    };

    private static MapAnnotation ToModel(AnnotationDto annotation) => new()
    {
        Id = annotation.Id,
        Type = annotation.Type == "text" ? MapAnnotationType.Text : MapAnnotationType.Outline,
        ColorIndex = annotation.ColorIndex,
        Bounds = ToRequiredModel(annotation.Bounds),
        Text = annotation.Text
    };

    private static void ValidateRectangle(RectangleDto? rectangle, bool allowNull, string name)
    {
        if (rectangle is null)
        {
            if (allowNull)
                return;
            throw new InvalidDataException($"{name} 不能为空。");
        }
        if (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y)
            || !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)
            || rectangle.X < -CoordinateTolerance || rectangle.Y < -CoordinateTolerance
            || rectangle.Width <= 0d || rectangle.Height <= 0d
            || rectangle.X + rectangle.Width > 1d + CoordinateTolerance
            || rectangle.Y + rectangle.Height > 1d + CoordinateTolerance)
        {
            throw new InvalidDataException($"{name} 包含超出 0..1 的坐标。");
        }
    }

    private readonly record struct ParsedHeader(
        Guid PackageId,
        long CreatedAtUnixMilliseconds,
        byte[] ManifestSha256);

    private sealed class ManifestDto
    {
        public string Format { get; set; } = string.Empty;
        public string FormatVersion { get; set; } = string.Empty;
        public string PackageType { get; set; } = string.Empty;
        public Guid PackageId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string MinimumReader { get; set; } = string.Empty;
        public List<ManifestClassDto> Classes { get; set; } = [];
        public List<ManifestMapDto> Maps { get; set; } = [];
        public List<ManifestFileDto> Files { get; set; } = [];
        public CapabilitiesDto Capabilities { get; set; } = new();
    }

    private sealed class ManifestClassDto
    {
        public Guid ClassId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Guid> MapIds { get; set; } = [];
    }

    private sealed class ManifestMapDto
    {
        public Guid MapId { get; set; }
        public int MapVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid ClassId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Root { get; set; } = string.Empty;
        public List<ManifestFloorDto> Floors { get; set; } = [];
    }

    private sealed class ManifestFloorDto
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string Image { get; set; } = string.Empty;
    }

    private sealed class ManifestFileDto
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class CapabilitiesDto
    {
        public bool MultiClass { get; set; } = true;
        public bool MultiFloor { get; set; } = true;
        public bool RecognitionAnchors { get; set; } = true;
        public bool DerivedCache { get; set; }
    }

    private sealed class MetadataDto
    {
        public int SchemaVersion { get; set; } = 1;
        public MetadataMapDto Map { get; set; } = new();
        public List<MetadataFloorDto> Floors { get; set; } = [];
        public RecognitionSettingsDto Recognition { get; set; } = new();
    }

    private sealed class MetadataMapDto
    {
        public Guid Id { get; set; }
        public Guid ClassId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string CoordinateSystem { get; set; } = string.Empty;
    }

    private sealed class MetadataFloorDto
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string Image { get; set; } = string.Empty;
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public int OrientationDegrees { get; set; }
        public RectangleDto? RecognitionRegion { get; set; }
        public RectangleDto ValidMapBounds { get; set; } = new() { Width = 1d, Height = 1d };
        /// <summary>侧门特征图元数据（可选，旧版 IDVM 包不含此字段）。</summary>
        public SideEntranceFeatureDto? SideEntranceFeature { get; set; }
    }

    /// <summary>侧门特征图的可移植元数据，写入 metadata.json。</summary>
    private sealed class SideEntranceFeatureDto
    {
        /// <summary>特征图在 IDVM 包内的逻辑路径（{root}/data/floor-n-side-entrance-feature.png）。</summary>
        public string File { get; set; } = string.Empty;
        /// <summary>实际中心点 X（识别图像素，边界挤压后）。</summary>
        public double CenterX { get; set; }
        /// <summary>实际中心点 Y（识别图像素，边界挤压后）。</summary>
        public double CenterY { get; set; }
        /// <summary>实际裁剪半径（像素）。</summary>
        public int Radius { get; set; }
    }

    private sealed class RecognitionSettingsDto
    {
        public int SchemaVersion { get; set; } = 1;
        public WholeImageDto WholeImage { get; set; } = new();
    }

    private sealed class WholeImageDto
    {
        public bool Enabled { get; set; }
        public double Weight { get; set; } = 0.15d;
        public double AnnotatedReferencePenalty { get; set; } = 0.55d;
        public bool ReferenceMayContainAnnotations { get; set; } = true;
    }

    private sealed class GatesDto
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GateDto> Gates { get; set; } = [];
    }

    private sealed class GateDto
    {
        public string Id { get; set; } = string.Empty;
        public string FloorKey { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public RectangleDto Bounds { get; set; } = new();
        public double DirectionDegrees { get; set; }
        public bool Enabled { get; set; } = true;
        public double Confidence { get; set; } = 1d;
    }

    private sealed class AnchorsDto
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, AnchorFloorDto> Floors { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AnchorFloorDto
    {
        public List<AnchorDto> Anchors { get; set; } = [];
        public List<RectangleDto> WholeImageIgnoreRegions { get; set; } = [];
        public List<AnnotationDto> Annotations { get; set; } = [];
    }

    private sealed class AnchorDto
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public double Weight { get; set; }
        public RectangleDto? Bounds { get; set; }
        public bool BuiltIn { get; set; }
        public string? GateId { get; set; }
    }

    private sealed class AnnotationDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public int ColorIndex { get; set; }
        public RectangleDto Bounds { get; set; } = new();
        public string? Text { get; set; }
    }

    private sealed class RectangleDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
