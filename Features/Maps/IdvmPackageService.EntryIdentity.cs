using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private sealed partial class CapabilitiesDto
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool EntryIdentityAssets { get; set; }
    }

    private sealed partial class MetadataFloorDto
    {
        // File names are leaves in this map's existing data/ directory.
        public MapEntryIdentityAsset? EntryIdentityAsset { get; set; }
    }

    private static MapEntryIdentityAsset? ReadImportedEntryIdentity(string root,
        ManifestDto manifest, ManifestMapDto map, MetadataFloorDto floor)
    {
        if (floor.EntryIdentityAsset is not { } asset) return null;
        if (manifest.FormatVersion != "1.4" || !manifest.Capabilities.EntryIdentityAssets)
            throw new InvalidDataException("入口身份资源要求 IDVM 1.4 和 entryIdentityAssets 能力。");
        if (asset.SourceWidth != floor.ImageWidth || asset.SourceHeight != floor.ImageHeight
            || floor.OrientationDegrees != 0 || floor.FreeCropPoints.Count != 0
            || floor.RecognitionRegion is { } region
                && (Math.Abs(region.X) > CoordinateTolerance || Math.Abs(region.Y) > CoordinateTolerance
                    || Math.Abs(region.Width - 1) > CoordinateTolerance || Math.Abs(region.Height - 1) > CoordinateTolerance)
            || !string.Equals(asset.SourceImageSha256,
                manifest.Files.Single(file => file.Path == floor.Image).Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口身份资源不对应本楼层的完整原图画布。");
        foreach (var fileName in MapEntryIdentityResources.EnumerateFileNames(asset))
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
                || fileName.Contains('/') || fileName.Contains('\\'))
                throw new InvalidDataException("入口身份资源必须使用地图 data 目录内的文件名。");
            ValidateLogicalPath($"{map.Root}/data/{fileName}");
        }
        var directory = ToPhysicalPath(root, $"{map.Root}/data");
        MapEntryIdentityResources.ValidateFiles(directory, asset, map.MapId, floor.Key);
        return asset.Clone();
    }

    private async Task<MapEntryIdentityAsset?> ExportEntryIdentityAsync(string staging, string root,
        MapRecord map, FloorDefinition floor, bool isDownsampled, CancellationToken cancellationToken)
    {
        if (floor.EntryIdentityAsset is not { } asset) return null;
        if (isDownsampled)
            throw new InvalidOperationException("入口身份资源绑定完整原图，导出前请恢复该地图类的原图尺寸。");
        var directory = Path.GetDirectoryName(_repository.GetFloorImagePath(map, floor.Key))!;
        using var verified = MapEntryIdentityResources.Load(directory, map, floor);
        foreach (var fileName in MapEntryIdentityResources.EnumerateFileNames(asset))
        {
            var destination = ToPhysicalPath(staging, $"{root}/data/{fileName}");
            if (File.Exists(destination))
                throw new InvalidDataException("入口身份资源与已导出的地图文件名冲突。");
            await CopyFileAsync(Path.Combine(directory, fileName), destination, cancellationToken);
        }
        // An exported local map receives its current ID in the manifest. Keep
        // author provenance intact while rebinding the passport to that package ID.
        var passportPath = ToPhysicalPath(staging, $"{root}/data/{asset.PassportFileName}");
        var passport = JsonNode.Parse(await File.ReadAllTextAsync(passportPath, cancellationToken))!.AsObject();
        passport["packageMapId"] = map.Id.ToString("D");
        await File.WriteAllTextAsync(passportPath, passport.ToJsonString(JsonOptions), cancellationToken);
        return asset.Clone();
    }
}
