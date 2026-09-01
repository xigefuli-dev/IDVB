using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
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
        if (manifest.Classes is null || manifest.Maps is null
            || manifest.VariantGroups is null || manifest.Files is null
            || manifest.Capabilities is null)
        {
            throw new InvalidDataException(
                "manifest 的 classes、maps、variantGroups、files 和 capabilities 必须有效。");
        }
        var isVersion10 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 0
            && manifest.FormatVersion == "1.0"
            && manifest.MinimumReader == "1.0";
        var isVersion11 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 1
            && manifest.FormatVersion == "1.1"
            && manifest.MinimumReader == "1.1";
        var isVersion12 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 2
            && manifest.FormatVersion == "1.2"
            && manifest.MinimumReader == "1.2";
        var isVersion13 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 3
            && manifest.FormatVersion == "1.3"
            && manifest.MinimumReader == "1.3";
        if (manifest.Format != "idvm"
            || (!isVersion10 && !isVersion11 && !isVersion12 && !isVersion13)
            || manifest.PackageType != "class-set")
        {
            throw new InvalidDataException("不支持的 IDVM 格式或读取器版本。");
        }
        if (isVersion10 && manifest.VariantGroups.Count != 0)
            throw new InvalidDataException("IDVM 1.0 包不能声明变体组合。");
        if (isVersion11 && !manifest.Capabilities.VariantGroups)
            throw new InvalidDataException("IDVM 1.1 包必须声明 variantGroups 能力。");
        if (isVersion12 && !manifest.Capabilities.FloorMarkerKeys)
            throw new InvalidDataException("IDVM 1.2 包必须声明 floorMarkerKeys 能力。");
        if (isVersion13 && (!manifest.Capabilities.FloorMarkerKeys || !manifest.Capabilities.MapTags))
            throw new InvalidDataException("IDVM 1.3 包必须声明 floorMarkerKeys 和 mapTags 能力。");
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

        ValidateManifestRelationships(manifest, isVersion12 || isVersion13);
        return manifest;
    }

    private static void ValidateManifestRelationships(
        ManifestDto manifest,
        bool allowMarkers)
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
            ValidateFloors(map.Floors, expectedRoot, allowMarkers);
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

        var mapClassById = manifest.Maps.ToDictionary(map => map.MapId, map => map.ClassId);
        var groupIds = new HashSet<Guid>();
        var groupedMapIds = new HashSet<Guid>();
        var paletteSlotsByClass = new Dictionary<Guid, HashSet<int>>();
        var groupCountsByClass = new Dictionary<Guid, int>();
        foreach (var group in manifest.VariantGroups)
        {
            if (group is null || group.MapIds is null
                || group.GroupId == Guid.Empty || !groupIds.Add(group.GroupId)
                || !classIds.Contains(group.ClassId)
                || group.PaletteSlot is < 0 or >= MapVariantGroup.PaletteSize
                || group.MapIds.Count < 2
                || group.MapIds.Count != group.MapIds.Distinct().Count())
            {
                throw new InvalidDataException("manifest 包含无效或重复的变体组合。");
            }

            if (!paletteSlotsByClass.TryGetValue(group.ClassId, out var paletteSlots))
            {
                paletteSlots = [];
                paletteSlotsByClass.Add(group.ClassId, paletteSlots);
            }
            if (!paletteSlots.Add(group.PaletteSlot))
                throw new InvalidDataException("同一 Class 的变体组合颜色槽不能重复。");

            groupCountsByClass.TryGetValue(group.ClassId, out var groupCount);
            if (groupCount >= MapVariantGroup.PaletteSize)
                throw new InvalidDataException("同一 Class 的变体组合不能超过 12 组。");
            groupCountsByClass[group.ClassId] = groupCount + 1;

            foreach (var mapId in group.MapIds)
            {
                if (!mapClassById.TryGetValue(mapId, out var mapClassId)
                    || mapClassId != group.ClassId)
                {
                    throw new InvalidDataException("变体组合包含缺失地图或跨 Class 成员。");
                }
                if (!groupedMapIds.Add(mapId))
                    throw new InvalidDataException("同一地图不能属于多个变体组合。");
            }
        }
    }

    private static void ValidateFloors(
        IReadOnlyList<ManifestFloorDto> floors,
        string root,
        bool allowMarkers)
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
            ValidateMarkerKeys(floor.MarkerKeys, allowMarkers);
            ValidateLogicalPath(floor.Image);
            if (!floor.Image.StartsWith($"{root}/maps/", StringComparison.Ordinal)
                || !MapRepository.IsSupportedImage(floor.Image))
                throw new InvalidDataException($"楼层 {floor.Key} 的图片路径无效。");
        }
    }

    private static void ValidateMapDocuments(
        ManifestMapDto map,
        MetadataDto metadata,
        GatesDto gates,
        AnchorsDto anchors,
        bool requireFloorMarkerSchema,
        bool requireTagSchema)
    {
        if (metadata.Map is null || metadata.Floors is null || metadata.Tags is null || metadata.Recognition?.WholeImage is null
            || gates.Gates is null || anchors.Floors is null)
            throw new InvalidDataException("地图数据文件缺少必需对象或数组。");
        if (metadata.SchemaVersion is not (1 or 2 or 3) || gates.SchemaVersion != 1
            || anchors.SchemaVersion is not (1 or 2 or 3 or 4))
            throw new InvalidDataException("不支持的数据 schemaVersion。");
        if (requireTagSchema && metadata.SchemaVersion != 3)
            throw new InvalidDataException("IDVM 1.3 地图 metadata 必须使用 schemaVersion 3。");
        if (requireFloorMarkerSchema && !requireTagSchema && metadata.SchemaVersion != 2)
        {
            throw new InvalidDataException(
                "IDVM 1.2 地图 metadata 必须使用 schemaVersion 2。");
        }
        if (!requireFloorMarkerSchema && metadata.SchemaVersion != 1)
            throw new InvalidDataException("IDVM 1.0/1.1 地图 metadata 必须使用 schemaVersion 1。");
        ValidateTags(metadata.Tags, requireTagSchema);
        if (metadata.Map.Id != map.MapId || metadata.Map.ClassId != map.ClassId
            || metadata.Map.CoordinateSystem != "normalized-top-left-y-down"
            || string.IsNullOrWhiteSpace(metadata.Map.Title))
            throw new InvalidDataException($"地图 {map.MapId} 的 metadata 标识不一致。");
        if (metadata.Floors.Count != map.Floors.Count)
            throw new InvalidDataException($"地图 {map.MapId} 的楼层清单不一致。");
        var backgroundLayerIds = new HashSet<Guid>();
        for (var index = 0; index < map.Floors.Count; index++)
        {
            var declared = map.Floors[index];
            var floor = metadata.Floors[index];
            if (floor.Key != declared.Key || floor.DisplayName != declared.DisplayName
                || floor.SortOrder != declared.SortOrder || floor.Image != declared.Image
                || !MapFloorMarkerRules.Normalize(floor.MarkerKeys).SequenceEqual(
                    MapFloorMarkerRules.Normalize(declared.MarkerKeys),
                    StringComparer.Ordinal)
                || floor.ImageWidth <= 0 || floor.ImageHeight <= 0
                || floor.OrientationDegrees is not (0 or 90 or 180 or 270)
                || !anchors.Floors.ContainsKey(floor.Key))
            {
                throw new InvalidDataException($"地图 {map.MapId} 的楼层 metadata 无效。");
            }
            ValidateMarkerKeys(floor.MarkerKeys, requireFloorMarkerSchema);
            ValidateRectangle(floor.RecognitionRegion, allowNull: true, "recognitionRegion");
            if (floor.FreeCropPoints.Count is 1 or 2)
                throw new InvalidDataException("freeCropPoints 必须为空或至少包含三个点。");
            foreach (var point in floor.FreeCropPoints)
                ValidatePoint(point, "freeCropPoints");
            ValidateRectangle(floor.ValidMapBounds, allowNull: false, "validMapBounds");
            if (!string.IsNullOrWhiteSpace(floor.RecognitionImage))
            {
                ValidateLogicalPath(floor.RecognitionImage);
                if (!floor.RecognitionImage.StartsWith($"{map.Root}/data/", StringComparison.Ordinal)
                    || !MapRepository.IsSupportedImage(floor.RecognitionImage))
                {
                    throw new InvalidDataException(
                        $"Floor {floor.Key} has an invalid recognition image path.");
                }
            }
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
                if (!IsValidAnnotation(annotation, anchors.SchemaVersion))
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效标注。");
                ValidateAnnotationGeometry(annotation);
            }
            if (anchors.SchemaVersion >= 4 && anchorFloor.BackgroundLayers is null)
                throw new InvalidDataException("schema 4 的 anchors.json 必须包含 backgroundLayers 数组。");
            foreach (var layer in anchorFloor.BackgroundLayers ?? [])
            {
                if (anchors.SchemaVersion < 4)
                    throw new InvalidDataException("旧版 anchors.json 不允许包含 backgroundLayers。");
                if (layer is null
                    || layer.Id == Guid.Empty
                    || !backgroundLayerIds.Add(layer.Id)
                    || layer.Semantic != "background"
                    || layer.Shape is not ("circle" or "square")
                    || layer.BrushSizePixels is < 1 or > 1024
                    || layer.Points is not { Count: > 0 })
                {
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效遮瑕层。");
                }
                foreach (var point in layer.Points)
                    ValidatePoint(point, "backgroundLayer.point");
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

    // ── 路径安全验证 ──────────────────────────────────────────────────

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

    // ── 验证工具方法 ──────────────────────────────────────────────────

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

    private static PointDto? ToDto(NormalizedPoint? point) => point is null ? null : new PointDto
    {
        X = point.X,
        Y = point.Y
    };

    private static bool IsValidAnnotation(AnnotationDto annotation, int schemaVersion)
    {
        if (annotation.Id == Guid.Empty)
            return false;
        if (schemaVersion == 1)
        {
            return annotation.Type is "text" or "outline"
                && annotation.ColorIndex is >= 0 and <= 8;
        }
        return annotation.Type is "text" or "outline" or "line"
            && MapAnnotationColor.TryNormalize(annotation.Color, out _);
    }

    private static void ValidateAnnotationGeometry(AnnotationDto annotation)
    {
        if (annotation.Type == "text")
            ValidateTextStyle(annotation);
        if (annotation.Type != "line")
        {
            ValidateRectangle(annotation.Bounds, allowNull: false, "annotation.bounds");
            if (annotation.Start is not null || annotation.End is not null)
                throw new InvalidDataException("非直线标注不能包含端点。");
            return;
        }

        ValidatePoint(annotation.Start, "annotation.start");
        ValidatePoint(annotation.End, "annotation.end");
        if (Math.Abs(annotation.Start!.X - annotation.End!.X) <= 0.000001d
            && Math.Abs(annotation.Start.Y - annotation.End.Y) <= 0.000001d)
        {
            throw new InvalidDataException("直线标注的两个端点不能重合。");
        }
        ValidateRectangle(annotation.Bounds, allowNull: true, "annotation.bounds");
    }

    private static void ValidateTextStyle(AnnotationDto annotation)
    {
        if (annotation.FontFamily is { Length: > 256 }
            || annotation.FontFamily?.Any(char.IsControl) is true
            || annotation.FontSize is { } size && (!double.IsFinite(size) || size is < 1d or > 256d))
        {
            throw new InvalidDataException("文字标注样式无效。");
        }
    }

    private static void ValidateMarkerKeys(
        IReadOnlyList<string>? markerKeys,
        bool allowMarkers)
    {
        if (markerKeys is null)
            throw new InvalidDataException("楼层 markerKeys 必须为数组。");
        if (!allowMarkers && markerKeys.Count != 0)
            throw new InvalidDataException("IDVM 旧版本不能声明楼层 markerKeys。");
        if (markerKeys.Count > 32
            || markerKeys.Any(key => !MapFloorMarkerRules.IsValid(key)))
            throw new InvalidDataException("楼层 markerKeys 包含非法标记。");
        if (!MapFloorMarkerRules.Normalize(markerKeys).SequenceEqual(
                markerKeys,
                StringComparer.Ordinal))
            throw new InvalidDataException("楼层 markerKeys 必须小写、去重并稳定排序。");
    }
}
/*
 * 文件职责：IdvmPackageService.Validator。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
