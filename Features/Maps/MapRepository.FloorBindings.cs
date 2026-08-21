using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private void ValidateFloorDefinitions(MapRecord record)
    {
        var floors = MapFloorRules.GetOrderedFloors(record);
        if (floors.Count == 0)
            throw new InvalidOperationException($"Map {record.Id} has no floor definitions.");

        if (floors.Count != record.Floors.Count)
            throw new InvalidOperationException($"Map {record.Id} has an invalid floor definition list.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var sortOrders = new HashSet<int>();
        var profiles = new List<FloorRecognitionProfile>();
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
                || !string.Equals(profile.FloorKey, floor.Key, StringComparison.Ordinal)
                || profiles.Any(existing => ReferenceEquals(existing, profile)))
            {
                throw new InvalidOperationException(
                    $"Map {record.Id}, floor '{floor.Key}' has no matching recognition profile.");
            }
            profiles.Add(profile);
        }

        if (record.Recognition.Floors.Count != floors.Count
            || record.Recognition.Floors.Keys.Any(key => !keys.Contains(key)))
        {
            throw new InvalidOperationException(
                $"Map {record.Id} has orphaned recognition floor profiles.");
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
            var removeBackground = GetClassPropertiesForMigration(record.Class).RemoveBackground;
            CreateRecognitionAssets(
                sourcePath,
                recognitionPath,
                assetProfile,
                overlayPath,
                removeBackground);
            await PopulateDerivedImageMetadataAsync(
                floor,
                sourcePath,
                recognitionPath,
                overlayPath,
                assetProfile,
                forceRecognitionPath: removeBackground || assetProfile.BackgroundLayers.Count > 0);
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
        if (profile is not null && UsesWholeSourceImage(profile)
            && profile.BackgroundLayers.Count == 0)
            return GetFloorImagePath(record, floorKey);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorRecognitionFileName(floorKey));
    }

    private MapClassProperties GetClassPropertiesForMigration(string className)
    {
        if (!File.Exists(CatalogPath))
            return new MapClassProperties();
        try
        {
            using var stream = File.OpenRead(CatalogPath);
            var catalog = System.Text.Json.JsonSerializer.Deserialize<MapCatalogDocument>(stream, SerializerOptions);
            return catalog?.ClassProperties?.FirstOrDefault(pair =>
                string.Equals(pair.Key, className, StringComparison.OrdinalIgnoreCase)).Value?.Clone()
                ?? new MapClassProperties();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new MapClassProperties();
        }
    }
}
/*
 * 文件职责：MapRepository.FloorBindings。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
