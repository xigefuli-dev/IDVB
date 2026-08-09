using OpenCvSharp;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
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

    private static bool UsesWholeSourceImage(FloorRecognitionProfile profile)
    {
        var region = profile.RecognitionRegion;
        return region?.IsValid is not true
            || (region.X <= 0.000001d
                && region.Y <= 0.000001d
                && region.X + region.Width >= 0.999999d
                && region.Y + region.Height >= 0.999999d);
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
