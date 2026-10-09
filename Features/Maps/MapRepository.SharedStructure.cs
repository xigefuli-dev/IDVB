using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    /// <summary>
    /// Selects an existing reference for a maker draft. No catalog or player
    /// state is changed, and no binary features are regenerated.
    /// </summary>
    public async Task ReuseFloorStructureAsync(
        MapDraft draft, string floorKey, Guid referenceMapId, string referenceFloorKey,
        MapArtworkRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registration.Validate();
        var reference = await CreateDraftAsync(referenceMapId)
            ?? throw new InvalidOperationException("所选结构参照地图已不存在。");
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = await GetCatalogSnapshotAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var map = catalog.Maps.SingleOrDefault(candidate => candidate.Id == referenceMapId)
            ?? throw new InvalidOperationException("所选结构参照地图已不存在。");
        var source = map.Floors.SingleOrDefault(floor => floor.Key == referenceFloorKey)
            ?? throw new InvalidOperationException("所选结构参照楼层已不存在。");
        source.SharedStructure?.Source?.Validate(floorKey);
        var target = draft.Floors.Single(floor => floor.Key == floorKey);
        registration = registration.Clone();
        if (!registration.SourceCropConfigured && registration.SourceCropRegion is null
            && registration.SourceCropPoints.Count == 0)
        {
            if (target.SharedStructure is null)
                registration.SetSourceCrop(draft.Recognition.GetFloor(floorKey));
            else if (target.ArtworkRegistration is { } previous)
            {
                // A shared floor's profile describes the canonical canvas;
                // only its saved registration still owns the author-sheet crop.
                if (previous.SourceWidth != registration.SourceWidth
                    || previous.SourceHeight != registration.SourceHeight)
                    throw new InvalidOperationException("小抄图片尺寸已经变化，请重新选择本层范围。");
                registration.SourceCropRegion = previous.SourceCropRegion?.Clone();
                registration.SourceCropPoints = previous.SourceCropPoints.Select(point => point.Clone()).ToList();
                registration.SourceCropConfigured = previous.SourceCropConfigured;
                registration.Validate();
            }
        }
        var canonicalPath = GetFloorRecognitionPath(map, referenceFloorKey);
        using var canonical = DecodeImage(canonicalPath);
        if (canonical.Empty() || canonical.Width != registration.ReferenceWidth
            || canonical.Height != registration.ReferenceHeight)
            throw new InvalidOperationException("结构参照图片已经变化，请重新配准。");
        if (source.PrebuiltStructureLine?.IsCurrent is not true)
            throw new InvalidOperationException("所选楼层没有可复用的结构二值图，请先制作结构资源。");
        if (reference.Recognition.GetFloor(referenceFloorKey)?.OrientationDegrees != 0)
            throw new InvalidOperationException("所选底图仍配置了旋转朝向，请先在结构制作中规范朝向，再复用该楼层。");
        var annotations = draft.Recognition.GetFloor(floorKey)?.Annotations ?? [];
        if (target.SharedStructure is not null && annotations.Count > 0
            && target.PrebuiltStructureLine?.SourceSha256 != source.PrebuiltStructureLine.SourceSha256)
            throw new InvalidOperationException("当前标注属于另一份结构底图，请先对准标注后再更换底图；现有标注会保留。");
        var registeredAnnotations = target.SharedStructure is null
            ? RegisterArtworkAnnotations(annotations, registration)
            : annotations.Select(annotation => annotation.Clone()).ToList();
        target.SharedStructure = source.SharedStructure?.Clone() ?? new MapSharedFloorStructure
        {
            UpdatedAt = map.UpdatedAt
        };
        target.ArtworkRegistration = registration.Clone();
        target.PrebuiltStructureLine = source.PrebuiltStructureLine.Clone();
        draft.FloorRecognitionSourcePaths[floorKey] = canonicalPath;
        draft.PrebuiltStructureLinePaths[floorKey] = GetPrebuiltStructureLinePath(map, referenceFloorKey);
        draft.PrebuiltStructureAlgorithmPath = GetPrebuiltStructureAlgorithmPath(map, referenceFloorKey);
        var profile = reference.Recognition.GetFloor(referenceFloorKey)!.Clone();
        profile.FloorKey = floorKey;
        // All maker geometry uses the finalized reference canvas.
        profile.OrientationDegrees = 0;
        profile.RecognitionRegion = null;
        profile.FreeCropPoints.Clear();
        profile.BackgroundLayers.Clear();
        profile.Annotations = registeredAnnotations;
        draft.Recognition.Floors[floorKey] = profile;
        draft.SharedStructureSourceProfiles[floorKey] = profile.Clone();
        draft.PortableGates.RemoveAll(gate => string.Equals(gate.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase));
        foreach (var gate in reference.PortableGates.Where(gate => string.Equals(gate.FloorKey, referenceFloorKey, StringComparison.OrdinalIgnoreCase)))
        {
            var copied = gate.Clone();
            copied.Id = $"{floorKey}-{gate.Id}";
            copied.FloorKey = floorKey;
            draft.PortableGates.Add(copied);
        }
        if (reference.SideEntranceFeaturePaths.TryGetValue(referenceFloorKey, out var featurePath))
            draft.SideEntranceFeaturePaths[floorKey] = featurePath;
        else
            draft.SideEntranceFeaturePaths.Remove(floorKey);
    }

    private async Task SaveSharedFloorAssetsAsync(
        string directory, FloorDefinition floor, FloorRecognitionProfile profile,
        string artworkPath, MapDraft draft, MapRecord? previous)
    {
        if (profile.OrientationDegrees != 0 || !UsesWholeSourceImage(profile) || profile.FreeCropPoints.Count > 0
            || profile.BackgroundLayers.Count > 0)
            throw new InvalidOperationException("复用的结构底图不能在小抄制作中裁剪、旋转或遮瑕，请在原结构制作中修改底图。");
        if (!draft.FloorRecognitionSourcePaths.TryGetValue(floor.Key, out var canonicalSource)
            || !File.Exists(canonicalSource))
            throw new InvalidOperationException($"楼层“{floor.DisplayName}”缺少结构底图；不能用小抄替代。");
        var registration = floor.ArtworkRegistration
            ?? throw new InvalidOperationException($"请先将楼层“{floor.DisplayName}”的小抄对准结构底图。");
        registration.Validate();
        var recognitionPath = Path.Combine(directory, GetFloorRecognitionFileName(floor.Key));
        await CopyRecognitionSourceAsync(canonicalSource, recognitionPath);
        using var canonical = DecodeImage(recognitionPath);
        if (canonical.Empty() || canonical.Width != registration.ReferenceWidth
            || canonical.Height != registration.ReferenceHeight)
            throw new InvalidOperationException("配准目标与结构底图尺寸不一致，请重新配准。");
        profile.RecognitionPixelWidth = canonical.Width;
        profile.RecognitionPixelHeight = canonical.Height;
        var overlayPath = Path.Combine(directory, GetFloorOverlayFileName(floor.Key));
        CreateRegisteredArtworkOverlay(artworkPath, recognitionPath, overlayPath, registration);
        await PopulateDerivedImageMetadataAsync(floor, artworkPath, recognitionPath, overlayPath,
            profile, forceRecognitionPath: true);
        // The canonical image is already finalized; artwork is a separate source.
        floor.RecognitionSourceSha256 = floor.RecognitionSha256;
        floor.OverlaySourceSha256 = floor.ImageSha256;
        await ImportPrebuiltStructureLineAsync(directory, floor, floor.Key, draft);
        if (floor.PrebuiltStructureLine?.IsCurrent is not true)
            throw new InvalidOperationException("结构参照的二值资源不完整，无法保存为可复用底图。");
        var oldFloor = previous?.Floors.FirstOrDefault(item => item.Key == floor.Key);
        var oldProfile = previous?.Recognition.GetFloor(floor.Key);
        var inputsChanged = oldFloor?.SharedStructure?.Id == floor.SharedStructure!.Id
            && oldProfile is not null
            && (MapStructureRevisionRules.GetRecognitionInputs(oldProfile) != MapStructureRevisionRules.GetRecognitionInputs(profile)
                || oldFloor.RecognitionSha256 != floor.RecognitionSha256
                || oldFloor.PrebuiltStructureLine?.Sha256 != floor.PrebuiltStructureLine?.Sha256
                || !oldFloor.MarkerKeys.SequenceEqual(floor.MarkerKeys));
        var referenceInputsChanged = draft.SharedStructureSourceProfiles.TryGetValue(floor.Key, out var referenceProfile)
            && MapStructureRevisionRules.GetRecognitionInputs(referenceProfile) != MapStructureRevisionRules.GetRecognitionInputs(profile);
        if (inputsChanged || referenceInputsChanged)
        {
            floor.SharedStructure.Revision = checked((oldFloor?.SharedStructure?.Id == floor.SharedStructure.Id
                ? oldFloor.SharedStructure.Revision : floor.SharedStructure.Revision) + 1);
            floor.SharedStructure.UpdatedAt = DateTimeOffset.UtcNow;
            profile.SideEntranceFeatureFileName = string.Empty;
            profile.SideEntranceFeatureSha256 = string.Empty;
            profile.SideEntranceFeatureSourceSha256 = string.Empty;
            profile.SideEntranceFeatureAlgorithmVersion = string.Empty;
            await TryGenerateSideEntranceFeatureAsync(directory, profile);
        }
        else if (draft.SideEntranceFeaturePaths.TryGetValue(floor.Key, out var featurePath)
            && File.Exists(featurePath) && !string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
        {
            profile.SideEntranceFeatureFileName = GetSideEntranceFeatureFileName(floor.Key);
            var destination = GetSafeMapFilePath(directory, profile.SideEntranceFeatureFileName);
            await CopyRecognitionSourceAsync(featurePath, destination);
        }
        if (string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
            await TryGenerateSideEntranceFeatureAsync(directory, profile);
        var thumbnailPath = Path.Combine(directory, GetFloorThumbnailFileName(floor.Key));
        await CreateThumbnailAsync(overlayPath, thumbnailPath);
        await PopulateThumbnailMetadataAsync(floor, thumbnailPath);
    }

    private static List<MapAnnotation> RegisterArtworkAnnotations(
        IEnumerable<MapAnnotation> annotations, MapArtworkRegistration registration)
    {
        var matrix = registration.SourceToReference;
        NormalizedPoint MapPoint(NormalizedPoint point)
        {
            var x = point.X * registration.SourceWidth;
            var y = point.Y * registration.SourceHeight;
            return new NormalizedPoint
            {
                X = (matrix[0] * x + matrix[1] * y + matrix[2]) / registration.ReferenceWidth,
                Y = (matrix[3] * x + matrix[4] * y + matrix[5]) / registration.ReferenceHeight
            };
        }
        var result = new List<MapAnnotation>();
        foreach (var annotation in annotations)
        {
            var mapped = annotation.Clone();
            if (mapped.Start is { } start) mapped.Start = MapPoint(start);
            if (mapped.End is { } end) mapped.End = MapPoint(end);
            if (mapped.Bounds is { } bounds)
            {
                var corners = new[] { (bounds.X, bounds.Y), (bounds.X + bounds.Width, bounds.Y),
                    (bounds.X, bounds.Y + bounds.Height), (bounds.X + bounds.Width, bounds.Y + bounds.Height) }
                    .Select(point => MapPoint(new NormalizedPoint { X = point.Item1, Y = point.Item2 })).ToArray();
                mapped.Bounds = new NormalizedRectangle { X = corners.Min(point => point.X), Y = corners.Min(point => point.Y),
                    Width = corners.Max(point => point.X) - corners.Min(point => point.X),
                    Height = corners.Max(point => point.Y) - corners.Min(point => point.Y) };
            }
            if (!mapped.IsValid)
                throw new InvalidOperationException("现有标注落在结构底图之外，请先调整标注再复用；原标注会保留。");
            result.Add(mapped);
        }
        return result;
    }

    private static void CreateRegisteredArtworkOverlay(
        string artworkPath, string canonicalPath, string overlayPath, MapArtworkRegistration registration)
    {
        using var artwork = DecodeImage(artworkPath);
        using var canonical = DecodeImage(canonicalPath);
        using var registered = MapArtworkRegistrationService.BakeOverlay(artwork, canonical, registration);
        if (!Cv2.ImWrite(overlayPath, registered))
            throw new InvalidOperationException("无法保存已配准的小抄图层。");
    }

    private bool EnsureSharedFloorDerivedAssets(
        MapRecord map, FloorDefinition floor, FloorRecognitionProfile profile)
    {
        var canonicalPath = GetFloorRecognitionPath(map, floor.Key);
        if (!MatchesStoredDerivedMetadata(canonicalPath, floor.RecognitionSha256,
            floor.RecognitionWidth, floor.RecognitionHeight, floor.RecognitionFileLength,
            floor.RecognitionLastWriteUtcTicks, floor.RecognitionSha256, requiresFile: true))
            throw new InvalidOperationException($"{map.DisplayName} 的楼层“{floor.DisplayName}”结构底图缺失或损坏，请重新导入底图。");
        var registration = floor.ArtworkRegistration
            ?? throw new InvalidOperationException("共享结构地图缺少制作时的小抄配准记录。");
        registration.Validate();
        if (registration.ReferenceWidth != floor.RecognitionWidth
            || registration.ReferenceHeight != floor.RecognitionHeight)
            throw new InvalidOperationException("小抄配准与结构底图尺寸不一致，请重新制作该楼层。");
        var overlayPath = GetFloorOverlayPath(map, floor.Key);
        var overlayMatches = MatchesStoredDerivedMetadata(overlayPath, floor.OverlaySha256,
            floor.OverlayWidth, floor.OverlayHeight, floor.OverlayFileLength,
            floor.OverlayLastWriteUtcTicks, floor.ImageSha256, requiresFile: true)
            && string.Equals(floor.OverlaySourceSha256, floor.ImageSha256, StringComparison.OrdinalIgnoreCase)
            && floor.OverlayWidth == floor.RecognitionWidth && floor.OverlayHeight == floor.RecognitionHeight;
        var changed = profile.RecognitionPixelWidth != floor.RecognitionWidth
            || profile.RecognitionPixelHeight != floor.RecognitionHeight;
        profile.RecognitionPixelWidth = floor.RecognitionWidth;
        profile.RecognitionPixelHeight = floor.RecognitionHeight;
        if (!overlayMatches)
        {
            var sourcePath = GetFloorImagePath(map, floor.Key);
            CreateRegisteredArtworkOverlay(sourcePath, canonicalPath, overlayPath, registration);
            PopulateDerivedImageMetadataAsync(floor, sourcePath, canonicalPath, overlayPath,
                profile, forceRecognitionPath: true).GetAwaiter().GetResult();
            floor.RecognitionSourceSha256 = floor.RecognitionSha256;
            floor.OverlaySourceSha256 = floor.ImageSha256;
            changed = true;
        }
        return changed;
    }
}
