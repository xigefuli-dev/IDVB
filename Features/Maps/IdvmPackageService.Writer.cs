using OpenCvSharp;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
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

    private static void WriteRfc4122Guid(Guid value, Span<byte> destination)
    {
        var raw = Convert.FromHexString(value.ToString("N"));
        raw.CopyTo(destination);
    }

    private static byte[] SerializeUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path, SerializeUtf8(value), cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
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
}
