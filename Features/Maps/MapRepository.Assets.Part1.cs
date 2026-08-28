using OpenCvSharp;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;
public sealed partial class MapRepository
{

    public async Task EnsureDerivedAssetsAsync(IReadOnlyList<MapRecord> maps)
    {
        await Gate.WaitAsync();
        Dictionary<string, MapClassProperties> classProperties;
        try
        {
            var catalog = await ReadCatalogAsync();
            classProperties = catalog.ClassProperties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Gate.Release();
        }

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
                        var removeBackground = classProperties.TryGetValue(
                            map.Class,
                            out var properties)
                            && properties.RemoveBackground;
                        var needsIndependentRecognition = removeBackground
                            || profile.BackgroundLayers.Count > 0
                            || !UsesWholeSourceImage(profile);
                        var sourcePath = GetFloorImagePath(map, floor.Key);
                        var recognitionPath = needsIndependentRecognition
                            ? Path.Combine(
                                GetMapDirectory(map.Id),
                                GetFloorRecognitionFileName(floor.Key))
                            : GetFloorRecognitionPath(map, floor.Key);
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
                            requiresFile: needsIndependentRecognition);
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
                                overlayPath,
                                removeBackground);
                            PopulateDerivedImageMetadataAsync(
                                floor,
                                sourcePath,
                                recognitionPath,
                                overlayPath,
                                profile,
                                forceRecognitionPath: needsIndependentRecognition).GetAwaiter().GetResult();
                            changed = true;
                        }

                        // Side-entrance features are derived from the recognition
                        // image as well. Missing, stale, or legacy features must
                        // be regenerated before the recognition cache is built.
                        changed |= EnsureCurrentSideEntranceFeature(
                            map,
                            floor.Key,
                            profile,
                            recognitionPath);

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
                        storedProfile.SideEntranceFeatureFileName =
                            sourceProfile.SideEntranceFeatureFileName;
                        storedProfile.SideEntranceFeatureSha256 =
                            sourceProfile.SideEntranceFeatureSha256;
                        storedProfile.SideEntranceFeatureSourceSha256 =
                            sourceProfile.SideEntranceFeatureSourceSha256;
                        storedProfile.SideEntranceFeatureAlgorithmVersion =
                            sourceProfile.SideEntranceFeatureAlgorithmVersion;
                        storedProfile.SideEntranceFeatureCenterX =
                            sourceProfile.SideEntranceFeatureCenterX;
                        storedProfile.SideEntranceFeatureCenterY =
                            sourceProfile.SideEntranceFeatureCenterY;
                        storedProfile.SideEntranceFeatureRadius =
                            sourceProfile.SideEntranceFeatureRadius;

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

        if (draft.Floors.Count > 0)
        {
            draft.Recognition.NormalizeForFloors(
                draft.Floors
                    .OrderBy(floor => floor.SortOrder)
                    .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                    .ToArray());
        }
        else
        {
            draft.Recognition.EnsureStandardAnchors();
        }
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
