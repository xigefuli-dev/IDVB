using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

/// <summary>Source renderer provenance, not a claim of walkability or wall completeness.</summary>
public sealed class MapTilemapFloorSource
{
    public string LayoutId { get; set; } = string.Empty;
    public int WorldFloor { get; set; }
    public string FloorKey { get; set; } = string.Empty;
    public int CellPixels { get; set; }
    public List<int> UnrenderedRoomIndices { get; set; } = [];
    public List<MapTilemapEntrance> Entrances { get; set; } = [];

    public MapTilemapFloorSource Clone() => new()
    {
        LayoutId = LayoutId, WorldFloor = WorldFloor, FloorKey = FloorKey,
        CellPixels = CellPixels, UnrenderedRoomIndices = [.. UnrenderedRoomIndices],
        Entrances = Entrances.Select(item => item.Clone()).ToList()
    };

    public void Validate(string? targetFloorKey = null)
    {
        var expectedKey = WorldFloor switch { 1 => "B1", 2 => "1F", 3 => "2F", _ => "" };
        var roles = Entrances?.Select(entrance => entrance.Role).ToArray() ?? [];
        var allowed = WorldFloor switch
        {
            2 => new[] { "main-entrance", "side-entrance" },
            3 => new[] { "second-floor-primary" },
            _ => Array.Empty<string>()
        };
        if (string.IsNullOrWhiteSpace(LayoutId) || CellPixels <= 0 || expectedKey.Length == 0
            || FloorKey != expectedKey || Entrances is null
            || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length
            || roles.Any(role => !allowed.Contains(role, StringComparer.Ordinal))
            || Entrances.Any(entrance => entrance.Bounds?.IsValid is not true)
            || (WorldFloor == 2 && !roles.Contains("main-entrance"))
            || (WorldFloor == 3 && !roles.Contains("second-floor-primary")))
            throw new InvalidDataException("结构底图的楼层或真实入口记录无效。");
        var standardKey = WorldFloor switch { 1 => "b1f", 2 => "1f", 3 => "2f", _ => "" };
        if (targetFloorKey is "b1f" or "1f" or "2f" && targetFloorKey != standardKey)
            throw new InvalidDataException("所选底图属于另一个楼层，请分别选择每层自己的结构底图。");
    }

    public static async Task<MapTilemapFloorSource> ReadAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(directory, "source.json"), cancellationToken));
        var root = document.RootElement;
        var source = new MapTilemapFloorSource
        {
            LayoutId = root.GetProperty("layout").GetString() ?? string.Empty,
            WorldFloor = root.GetProperty("worldFloor").GetInt32(),
            FloorKey = root.GetProperty("floorKey").GetString() ?? string.Empty,
            CellPixels = root.GetProperty("cellPixels").GetInt32()
        };
        var expectedKey = source.WorldFloor switch { 1 => "B1", 2 => "1F", 3 => "2F", _ => "" };
        if (string.IsNullOrWhiteSpace(source.LayoutId) || source.CellPixels <= 0
            || expectedKey.Length == 0 || source.FloorKey != expectedKey)
            throw new InvalidDataException("结构底图的地图或楼层记录无效，请使用完整的源楼层文件夹。");
        if (root.GetProperty("render").GetProperty("diagnostics")
            .TryGetProperty("skippedRoomFloors", out var skipped))
            source.UnrenderedRoomIndices = skipped.EnumerateArray()
                .Select(item => item.GetProperty("roomIndex").GetInt32()).Distinct().Order().ToList();
        using var image = Cv2.ImRead(Path.Combine(directory, "rendered-base.png"), ImreadModes.Unchanged);
        if (image.Empty()) throw new InvalidDataException("结构底图无法读取。");
        var worldBounds = root.GetProperty("render").GetProperty("bounds");
        foreach (var exit in root.GetProperty("exits").EnumerateArray())
        {
            var kind = exit.GetProperty("kind").GetString();
            var role = kind switch
            {
                "front-door" => "main-entrance",
                "side-door" when source.FloorKey == "2F" => "second-floor-primary",
                "side-door" => "side-entrance", _ => null
            };
            if (role is null) continue;
            if (exit.GetProperty("floor").GetInt32() != source.WorldFloor)
                throw new InvalidDataException("源入口不属于当前楼层。");
            var point = exit.GetProperty("point").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            // Source world grid is 128 units per cell; connections.json uses
            // the same conversion. This is an anchor ROI, not inferred door width.
            var x = (point[0] * 128 - worldBounds.GetProperty("minX").GetDouble()) * source.CellPixels / 128;
            var y = (point[1] * 128 - worldBounds.GetProperty("minY").GetDouble()) * source.CellPixels / 128;
            var halfWidth = Math.Max(16, image.Width * 0.006);
            var halfHeight = Math.Max(16, image.Height * 0.006);
            if (x - halfWidth < 0 || y - halfHeight < 0 || x + halfWidth > image.Width || y + halfHeight > image.Height)
                throw new InvalidDataException("源入口坐标超出结构底图，请核对该层原始坐标。");
            source.Entrances.Add(new MapTilemapEntrance { Role = role,
                Bounds = new NormalizedRectangle { X = (x - halfWidth) / image.Width,
                    Y = (y - halfHeight) / image.Height, Width = 2 * halfWidth / image.Width,
                    Height = 2 * halfHeight / image.Height } });
        }
        source.Validate();
        return source;
    }
}

public sealed class MapTilemapEntrance
{
    public string Role { get; set; } = string.Empty;
    public NormalizedRectangle Bounds { get; set; } = new();
    public MapTilemapEntrance Clone() => new() { Role = Role, Bounds = Bounds.Clone() };
}

public sealed partial class MapRepository
{
    /// <summary>
    /// Maker entry for a source-rendered floor. Its IDVA line is generated from
    /// the same finalized PNG that Save/IDVM/runtime consume. NPZ fill, gradient
    /// and wall candidates are never relabelled as semantic walls.
    /// </summary>
    public async Task PrepareTilemapFloorStructureAsync(MapDraft draft, string floorKey,
        string sourceDirectory, string algorithmPath, MapArtworkRegistration registration,
        CancellationToken cancellationToken = default)
    {
        registration.Validate();
        var source = await MapTilemapFloorSource.ReadAsync(sourceDirectory, cancellationToken);
        source.Validate(floorKey);
        var target = draft.Floors.Single(floor => floor.Key == floorKey);
        var rendered = Path.Combine(sourceDirectory, "rendered-base.png");
        using var canonical = DecodeImage(rendered);
        using var artwork = DecodeImage(draft.FloorPaths[floorKey]);
        if (canonical.Empty() || artwork.Empty()
            || canonical.Width != registration.ReferenceWidth || canonical.Height != registration.ReferenceHeight
            || artwork.Width != registration.SourceWidth || artwork.Height != registration.SourceHeight)
            throw new InvalidOperationException("小抄或结构底图已变化，请重新对准该楼层。");
        if (target.SharedStructure is not null)
            throw new InvalidOperationException("该楼层已使用共享结构，请用复用入口更换小抄；更换真实布局需重新制作地图。");
        var profile = draft.Recognition.GetFloor(floorKey)?.Clone()
            ?? throw new InvalidOperationException("该楼层缺少制作配置。");
        registration = registration.Clone();
        if (!registration.SourceCropConfigured && registration.SourceCropRegion is null && registration.SourceCropPoints.Count == 0)
            registration.SetSourceCrop(profile);
        if (profile.OrientationDegrees != 0)
            throw new InvalidOperationException("请先将原小抄朝向归零，再对准结构底图。");
        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(algorithmPath, cancellationToken);
        var region = profile.GetEffectiveRecognitionRegion();
        NormalizedRectangle MapReferenceRectangle(NormalizedRectangle bounds)
        {
            var original = new NormalizedRectangle
            {
                X = region.X + bounds.X * region.Width, Y = region.Y + bounds.Y * region.Height,
                Width = bounds.Width * region.Width, Height = bounds.Height * region.Height
            };
            var annotation = new MapAnnotation { Type = MapAnnotationType.Outline, Bounds = original };
            return RegisterArtworkAnnotations([annotation], registration).Single().Bounds!;
        }
        profile.SourceEntranceRoles = source.Entrances.Select(entrance => entrance.Role).ToHashSet(StringComparer.Ordinal);
        foreach (var anchor in profile.Anchors.Where(anchor => anchor.IsBuiltIn
            && (anchor.Key is "main-entrance" or "side-entrance" or "second-floor-primary")
            && !profile.SourceEntranceRoles.Contains(anchor.Key)))
        {
            anchor.Bounds = null;
            anchor.Role = RecognitionAnchorRole.Optional;
        }
        foreach (var anchor in profile.Anchors.Where(anchor => anchor.Bounds is not null
            && !profile.SourceEntranceRoles.Contains(anchor.Key)))
            anchor.Bounds = MapReferenceRectangle(anchor.Bounds!);
        foreach (var entrance in source.Entrances)
        {
            var anchor = profile.FindAnchor(entrance.Role);
            if (anchor is null)
            {
                anchor = new RecognitionAnchor { Key = entrance.Role, DisplayName = entrance.Role,
                    IsBuiltIn = true, Role = RecognitionAnchorRole.Required, Weight = 1 };
                profile.Anchors.Add(anchor);
            }
            anchor.Bounds = entrance.Bounds.Clone();
            anchor.Role = RecognitionAnchorRole.Required;
        }
        var registeredGates = draft.PortableGates.Where(gate => gate.FloorKey == floorKey)
            .Where(gate => gate.Role switch
            {
                "mainEntrance" => profile.SourceEntranceRoles.Contains("main-entrance"),
                "sideEntrance" => profile.SourceEntranceRoles.Contains("side-entrance"),
                _ => true
            })
            .Select(gate => gate.Clone()).ToArray();
        foreach (var gate in registeredGates)
        {
            var anchorKey = gate.Role switch
            {
                "mainEntrance" => "main-entrance", "sideEntrance" => "side-entrance", _ => null
            };
            gate.Bounds = anchorKey is not null
                ? profile.FindAnchor(anchorKey)!.Bounds!.Clone() : MapReferenceRectangle(gate.Bounds);
            var matrix = registration.SourceToReference;
            var angle = gate.DirectionDegrees * Math.PI / 180;
            gate.DirectionDegrees = Math.Atan2(matrix[3] * Math.Cos(angle) + matrix[4] * Math.Sin(angle),
                matrix[0] * Math.Cos(angle) + matrix[1] * Math.Sin(angle)) * 180 / Math.PI;
        }
        profile.Annotations = RegisterArtworkAnnotations(profile.Annotations, registration);
        profile.WholeImageIgnoreRegions.Clear();
        profile.BackgroundLayers.Clear();
        profile.FreeCropPoints.Clear();
        profile.RecognitionRegion = null;
        profile.ValidMapBounds = null;
        profile.RecognitionPixelWidth = canonical.Width;
        profile.RecognitionPixelHeight = canonical.Height;
        profile.SideEntranceFeatureFileName = string.Empty;
        profile.SideEntranceFeatureSha256 = string.Empty;
        profile.SideEntranceFeatureSourceSha256 = string.Empty;
        profile.SideEntranceFeatureAlgorithmVersion = string.Empty;

        var staging = Path.Combine(_rootDirectory, ".maker-structure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // Preserve source bytes. Save/import do not decode and re-encode this canonical image.
        var canonicalPath = Path.Combine(staging, "recognition.png");
        await CopyRecognitionSourceAsync(rendered, canonicalPath);
        var binaryPath = Path.Combine(staging, "prebuilt.png");
        var result = await Task.Run(() => engine.Execute(algorithm, canonicalPath, binaryPath,
            cancellationToken: cancellationToken), cancellationToken);
        if (result.EdgePixels == 0)
            throw new InvalidDataException("所选制作算法未从结构底图提取到结构线，请核对算法与源图。");
        var asset = new PrebuiltStructureLineAsset
        {
            FileName = "prebuilt-" + floorKey + ".png",
            Sha256 = await ComputeFileSha256Async(binaryPath, cancellationToken),
            SourceSha256 = await ComputeFileSha256Async(canonicalPath, cancellationToken),
            Width = result.Width, Height = result.Height, FileLength = new FileInfo(binaryPath).Length,
            AlgorithmId = algorithm.AlgorithmId, AlgorithmFileName = "prebuilt-structure.idva",
            AlgorithmSha256 = algorithm.Sha256, AlgorithmSchemaVersion = algorithm.SchemaVersion,
            EngineRevision = IdvaStructureLineEngine.CurrentRevision
        };
        var algorithmCopy = Path.Combine(staging, "prebuilt-structure.idva");
        await File.WriteAllBytesAsync(algorithmCopy, algorithm.PackageBytes, cancellationToken);
        var previewPath = Path.Combine(staging, "artwork.png");
        CreateRegisteredArtworkOverlay(draft.FloorPaths[floorKey], canonicalPath, previewPath, registration);
        // Commit the prepared floor to the draft only after every operation succeeded.
        cancellationToken.ThrowIfCancellationRequested();
        target.SharedStructure = new MapSharedFloorStructure { Source = source };
        target.ArtworkRegistration = registration.Clone();
        target.PrebuiltStructureLine = asset;
        draft.Recognition.Floors[floorKey] = profile;
        draft.SharedStructureSourceProfiles[floorKey] = profile.Clone();
        draft.FloorRecognitionSourcePaths[floorKey] = canonicalPath;
        draft.FloorPreviewPaths[floorKey] = previewPath;
        draft.PrebuiltStructureLinePaths[floorKey] = binaryPath;
        draft.PrebuiltStructureAlgorithmPath = algorithmCopy;
        draft.SideEntranceFeaturePaths.Remove(floorKey);
        draft.PortableGates.RemoveAll(gate => gate.FloorKey == floorKey);
        draft.PortableGates.AddRange(registeredGates);
    }
}
