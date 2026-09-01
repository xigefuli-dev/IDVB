using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    public static int ClampImageDownsampleFactor(int value) =>
        value <= 1 ? 0 : Math.Clamp(value, 2, 8);

    public async Task<IReadOnlyList<Guid>> SetClassImageDownsamplingAsync(
        string className,
        int factor,
        bool? removeBackgroundOverride = null,
        int? backgroundRemovalIntensityOverride = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalName = NormalizeClassName(className)
            ?? throw new InvalidOperationException("Class 名称不能为空。");
        var normalizedFactor = ClampImageDownsampleFactor(factor);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadCatalogAsync();
            canonicalName = catalog.Classes.SingleOrDefault(candidate =>
                string.Equals(candidate, canonicalName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("找不到要修改的 Class。");
            var current = GetClassProperties(catalog, canonicalName);
            var targetRemoveBackground = removeBackgroundOverride
                ?? current.RemoveBackground;
            var targetIntensity = backgroundRemovalIntensityOverride is { } intensity
                ? MapBackgroundProcessor.ClampBackgroundRemovalIntensity(intensity)
                : current.BackgroundRemovalIntensity;
            if (ClampImageDownsampleFactor(current.ImageDownsampleFactor) == normalizedFactor
                && current.RemoveBackground == targetRemoveBackground
                && current.BackgroundRemovalIntensity == targetIntensity)
                return Array.Empty<Guid>();

            var maps = catalog.Maps
                .Where(map => string.Equals(map.Class, canonicalName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(map => map.SequenceNumber)
                .Select(map => map.Clone())
                .ToList();
            var originalCatalog = await File.ReadAllBytesAsync(CatalogPath, cancellationToken);
            var operationDirectory = Path.Combine(_rootDirectory, $".class-downsample-{Guid.NewGuid():N}");
            var mapBackups = Path.Combine(operationDirectory, "maps");
            var inputDirectory = Path.Combine(operationDirectory, "inputs");
            var originalsRoot = Path.Combine(_rootDirectory, ".downsample-originals");
            Directory.CreateDirectory(mapBackups);
            Directory.CreateDirectory(inputDirectory);
            var createdOriginals = new List<string>();
            try
            {
                foreach (var map in maps)
                {
                    var mapDirectory = GetMapDirectory(map.Id);
                    if (!Directory.Exists(mapDirectory))
                        throw new InvalidOperationException($"地图 {map.DisplayName} 的资源目录不存在。");
                    CopyDirectory(mapDirectory, Path.Combine(mapBackups, map.Id.ToString("N")));
                    var originalDirectory = GetDownsampleOriginalDirectory(originalsRoot, map.Id);
                }

                foreach (var map in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var draft = await CreateDraftCoreAsync(map.Id, gateAlreadyHeld: true)
                        ?? throw new InvalidOperationException($"地图 {map.DisplayName} 已不存在。");
                    var mapInputs = Path.Combine(inputDirectory, map.Id.ToString("N"));
                    Directory.CreateDirectory(mapInputs);
                    for (var index = 0; index < draft.Floors.Count; index++)
                    {
                        var floor = draft.Floors[index];
                        if (!draft.FloorPaths.TryGetValue(floor.Key, out var activeVisual))
                            continue;
                        var originalDirectory = GetDownsampleOriginalDirectory(originalsRoot, map.Id);
                        Directory.CreateDirectory(originalDirectory);
                        var originalVisual = EnsureOriginalImage(
                            activeVisual,
                            originalDirectory,
                            $"floor-{index + 1:D3}-visual",
                            createdOriginals);
                        draft.FloorPaths[floor.Key] = await CreateDownsampleInputAsync(
                            originalVisual,
                            mapInputs,
                            $"floor-{index + 1:D3}-visual",
                            normalizedFactor,
                            cancellationToken);

                        if (draft.FloorRecognitionSourcePaths.TryGetValue(floor.Key, out var activeRecognition)
                            && File.Exists(activeRecognition))
                        {
                            var originalRecognition = EnsureOriginalImage(
                                activeRecognition,
                                originalDirectory,
                                $"floor-{index + 1:D3}-recognition",
                                createdOriginals);
                            draft.FloorRecognitionSourcePaths[floor.Key] = await CreateDownsampleInputAsync(
                                originalRecognition,
                                mapInputs,
                                $"floor-{index + 1:D3}-recognition",
                                normalizedFactor,
                                cancellationToken);
                        }
                    }
                    ScaleBackgroundBrushes(
                        draft.Recognition,
                        current.ImageDownsampleFactor,
                        normalizedFactor);
                    draft.RemoveBackgroundOverride = targetRemoveBackground;
                    draft.BackgroundRemovalIntensityOverride = targetIntensity;
                    await SaveCoreAsync(draft, gateAlreadyHeld: true);
                }

                var updatedCatalog = await ReadCatalogAsync();
                var properties = GetClassProperties(updatedCatalog, canonicalName);
                properties.ImageDownsampleFactor = normalizedFactor;
                properties.RemoveBackground = targetRemoveBackground;
                properties.BackgroundRemovalIntensity = targetIntensity;
                updatedCatalog.ClassProperties[canonicalName] = properties;
                await WriteCatalogAsync(updatedCatalog);
                return maps.Select(map => map.Id).ToArray();
            }
            catch
            {
                foreach (var map in maps)
                {
                    var backup = Path.Combine(mapBackups, map.Id.ToString("N"));
                    var target = GetMapDirectory(map.Id);
                    if (Directory.Exists(backup))
                    {
                        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                        CopyDirectory(backup, target);
                    }

                }
                foreach (var path in createdOriginals)
                    if (File.Exists(path)) File.Delete(path);
                await File.WriteAllBytesAsync(CatalogPath, originalCatalog, CancellationToken.None);
                throw;
            }
            finally
            {
                if (Directory.Exists(operationDirectory))
                    Directory.Delete(operationDirectory, recursive: true);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string GetDownsampleOriginalDirectory(string root, Guid mapId) =>
        Path.Combine(root, mapId.ToString("N"));

    private static string EnsureOriginalImage(
        string source,
        string directory,
        string name,
        ICollection<string> createdOriginals)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var target = Path.Combine(directory, name + extension);
        if (!File.Exists(target))
        {
            File.Copy(source, target);
            createdOriginals.Add(target);
        }
        return target;
    }

    private static void ScaleBackgroundBrushes(
        MapRecognitionProfile recognition,
        int oldFactor,
        int newFactor)
    {
        var scale = (oldFactor <= 1 ? 1d : ClampImageDownsampleFactor(oldFactor))
            / (newFactor <= 1 ? 1d : ClampImageDownsampleFactor(newFactor));
        foreach (var floor in recognition.Floors.Values)
        foreach (var layer in floor.BackgroundLayers)
            layer.BrushSizePixels = ClampBrushSizeForImageScale(
                layer.BrushSizePixels,
                scale);
    }

    internal static int ClampBrushSizeForImageScale(int brushSize, double scale) =>
        MapBackgroundProcessor.ClampBrushSize(
            (int)Math.Round(brushSize * scale, MidpointRounding.AwayFromZero));

    private static async Task<string> CreateDownsampleInputAsync(
        string original,
        string destinationDirectory,
        string name,
        int factor,
        CancellationToken cancellationToken)
    {
        if (factor == 0)
            return original;
        var destination = Path.Combine(destinationDirectory, name + Path.GetExtension(original).ToLowerInvariant());
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = Cv2.ImRead(original, ImreadModes.Unchanged);
            if (source.Empty())
                throw new InvalidOperationException($"无法读取地图原图：{Path.GetFileName(original)}");
            using var resized = new Mat();
            Cv2.Resize(
                source,
                resized,
                new Size(Math.Max(1, source.Width / factor), Math.Max(1, source.Height / factor)),
                0,
                0,
                InterpolationFlags.Area);
            if (!Cv2.ImWrite(destination, resized))
                throw new InvalidOperationException($"无法保存地图降采样图片：{Path.GetFileName(original)}");
        }, cancellationToken);
        return destination;
    }
}
