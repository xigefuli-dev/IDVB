using OpenCvSharp;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
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

    private static Guid ReadRfc4122Guid(ReadOnlySpan<byte> source)
    {
        var hex = Convert.ToHexString(source);
        return Guid.ParseExact(hex, "N");
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
                Source = string.Equals(metadata.Map.Source, "survey", StringComparison.Ordinal)
                    ? "survey"
                    : "manual",
                SourceProjectId = metadata.Map.SourceProjectId,
                SourceProjectRevision = metadata.Map.SourceProjectRevision,
                SourceVisualSha256 = metadata.Map.SourceVisualSha256,
                SourceStructureSha256 = metadata.Map.SourceStructureSha256,
                Floors = floorDefinitions,
                FloorPaths = floorPaths,
                FloorPreviewPaths = new Dictionary<string, string>(floorPaths, StringComparer.Ordinal),
                FloorRecognitionSourcePaths = metadata.Floors
                    .Where(floor => !string.IsNullOrWhiteSpace(floor.RecognitionImage))
                    .ToDictionary(
                        floor => floor.Key,
                        floor => ToPhysicalPath(root, floor.RecognitionImage!),
                        StringComparer.Ordinal),
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

    // ── 导入 DTO 映射辅助方法 ──────────────────────────────────────────

    private static NormalizedRectangle? ToModel(RectangleDto? rectangle) => rectangle is null ? null : new NormalizedRectangle
    {
        X = Math.Clamp(rectangle.X, 0d, 1d),
        Y = Math.Clamp(rectangle.Y, 0d, 1d),
        Width = Math.Clamp(rectangle.Width, 0d, 1d),
        Height = Math.Clamp(rectangle.Height, 0d, 1d)
    };

    private static NormalizedRectangle ToRequiredModel(RectangleDto rectangle) => ToModel(rectangle)!;

    private static NormalizedPoint? ToModel(PointDto? point) => point is null ? null : new NormalizedPoint
    {
        X = Math.Clamp(point.X, 0d, 1d),
        Y = Math.Clamp(point.Y, 0d, 1d)
    };

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
        Type = annotation.Type switch
        {
            "text" => MapAnnotationType.Text,
            "outline" => MapAnnotationType.Outline,
            "line" => MapAnnotationType.Line,
            _ => default
        },
        ColorIndex = annotation.ColorIndex,
        ColorHex = annotation.Color,
        Bounds = ToModel(annotation.Bounds),
        Start = ToModel(annotation.Start),
        End = ToModel(annotation.End),
        Text = annotation.Text,
        FontFamily = annotation.FontFamily,
        FontSize = annotation.FontSize,
        IsBold = annotation.IsBold,
        IsItalic = annotation.IsItalic,
        IsStrikethrough = annotation.IsStrikethrough
    };
}
