using System.Text.Json;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>One floor's immutable geometry and binary channels, all in original author pixels.</summary>
public sealed class MapEntryIdentityResource : IDisposable
{
    public required string SourceMapId { get; init; }
    public required string SourceFloor { get; init; }
    public required string ProfileId { get; init; }
    public required string SourceCommit { get; init; }
    public required IReadOnlyList<MapEntryIdentityPassport> Passports { get; init; }
    public required IReadOnlyList<MapEntryCornerRegion> MainRegions { get; init; }
    public required IReadOnlyList<MapEntryCornerRegion> LocalRegions { get; init; }
    /// <summary>Full-floor measured wall planes in author pixels; empty EdgeId means no contour ID was supplied.</summary>
    public IReadOnlyList<MapEntryIdentityWall> NativeWalls { get; init; } = [];
    public required IReadOnlyList<JsonElement> SourceEntries { get; init; }
    public required IReadOnlyList<JsonElement> Coverage { get; init; }
    // Each CV_8UC1 Mat contains 0/1. Unknown means unobserved/unreconstructed, not empty space.
    public required Lazy<Mat> LazyCleanMask { private get; init; }
    public required Lazy<Mat> LazyKnownBoundary { private get; init; }
    public required Lazy<Mat> LazyUnknownMask { private get; init; }
    public required Lazy<Mat> LazyAuthorValidationMask { private get; init; }
    public Mat CleanMask => LazyCleanMask.Value;
    public Mat KnownBoundary => LazyKnownBoundary.Value;
    public Mat UnknownMask => LazyUnknownMask.Value;
    public Mat AuthorValidationMask => LazyAuthorValidationMask.Value;

    public void Dispose()
    {
        if (LazyCleanMask.IsValueCreated) LazyCleanMask.Value.Dispose();
        if (LazyKnownBoundary.IsValueCreated) LazyKnownBoundary.Value.Dispose();
        if (LazyUnknownMask.IsValueCreated) LazyUnknownMask.Value.Dispose();
        if (LazyAuthorValidationMask.IsValueCreated) LazyAuthorValidationMask.Value.Dispose();
    }
}

public static partial class MapEntryIdentityResources
{
    public static IReadOnlyList<string> EnumerateFileNames(MapEntryIdentityAsset asset) =>
        [asset.PassportFileName, asset.CleanMaskFileName, asset.KnownBoundaryFileName,
            asset.UnknownMaskFileName, asset.AuthorValidationMaskFileName];

    /// <summary>Used after the package's normal path, size and integrity validation.</summary>
    public static void ValidateFiles(string directory, MapEntryIdentityAsset asset,
        Guid packageMapId, string floorKey)
    {
        using var resource = Read(directory, asset, packageMapId, floorKey, packageMapId);
        _ = resource.CleanMask;
        _ = resource.KnownBoundary;
        _ = resource.UnknownMask;
        _ = resource.AuthorValidationMask;
    }

    public static MapEntryIdentityResource Load(string mapDirectory, MapRecord map, FloorDefinition floor)
    {
        var asset = floor.EntryIdentityAsset
            ?? throw new InvalidDataException("Floor has no entrance identity resource.");
        var profile = MapFloorRules.GetFloorProfile(map, floor.Key)
            ?? throw new InvalidDataException("Entrance resource has no floor profile.");
        var region = profile.RecognitionRegion;
        if (floor.ImageWidth != asset.SourceWidth || floor.ImageHeight != asset.SourceHeight
            || floor.RecognitionWidth != asset.SourceWidth || floor.RecognitionHeight != asset.SourceHeight
            || !string.Equals(floor.ImageSha256, asset.SourceImageSha256, StringComparison.OrdinalIgnoreCase)
            || profile.OrientationDegrees != 0 || profile.FreeCropPoints.Count != 0
            || region is not null && (Math.Abs(region.X) > 1e-6 || Math.Abs(region.Y) > 1e-6
                || Math.Abs(region.Width - 1) > 1e-6 || Math.Abs(region.Height - 1) > 1e-6))
            throw new InvalidDataException("Entrance resource no longer matches the floor author coordinate canvas.");
        return Read(mapDirectory, asset, map.SourcePackageMapId ?? map.Id, floor.Key, map.Id);
    }

    private static MapEntryIdentityResource Read(string directory, MapEntryIdentityAsset asset,
        Guid packageMapId, string floorKey, Guid localMapId)
    {
        if (asset.SchemaVersion != 1 || asset.SourceWidth <= 0 || asset.SourceHeight <= 0
            || asset.SourceWidth > 16384 || asset.SourceHeight > 16384
            || asset.SourceImageSha256.Length != 64 || packageMapId == Guid.Empty)
            throw new InvalidDataException("Invalid entrance resource declaration.");
        var names = EnumerateFileNames(asset);
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new InvalidDataException("Entrance resource files must be distinct.");
        foreach (var name in names)
            if (!File.Exists(SafePath(directory, name))) throw new InvalidDataException("Entrance resource file is missing.");
        var path = SafePath(directory, asset.PassportFileName);
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Entrance passport is too large.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("packageMapId").GetGuid() != packageMapId
            || root.GetProperty("floorKey").GetString() != floorKey
            || root.GetProperty("sourceWidth").GetInt32() != asset.SourceWidth
            || root.GetProperty("sourceHeight").GetInt32() != asset.SourceHeight
            || !string.Equals(Text(root, "sourceImageSha256"), asset.SourceImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Entrance passport map, floor or author image binding does not match.");
        var sourceMap = Text(root, "sourceMapId");
        var sourceFloor = Text(root, "sourceFloor");
        var profile = Text(root, "profileId");
        var entries = root.GetProperty("entries").EnumerateArray().Select(e => e.Clone()).ToArray();
        var coverage = root.GetProperty("coverage").EnumerateArray().Select(e => e.Clone()).ToArray();
        var mainRows = root.GetProperty("mainRegions").EnumerateArray().Select(e => e.Clone()).ToArray();
        var localRows = root.GetProperty("localRegions").EnumerateArray().Select(e => e.Clone()).ToArray();
        if (entries.Length > 256 || coverage.Length > 256)
            throw new InvalidDataException("Entrance resource contains too many entries.");
        foreach (var row in entries.Concat(coverage).Concat(mainRows).Concat(localRows))
            if (Text(row, "mapId") != sourceMap || Text(row, "floor") != sourceFloor)
                throw new InvalidDataException("Entrance geometry belongs to another source map or floor.");
        var passports = entries.Select(e => ParsePassport(e, localMapId.ToString(), floorKey)).ToArray();
        Lazy<Mat> Mask(string name) => new(() => ReadMask(SafePath(directory, name), asset));
        return new MapEntryIdentityResource
        {
            SourceMapId = sourceMap, SourceFloor = sourceFloor, ProfileId = profile,
            SourceCommit = Text(root, "sourceCommit"), Passports = passports,
            MainRegions = mainRows.Select(e => ParseMainRegion(e, localMapId.ToString(), floorKey, profile)).ToArray(),
            LocalRegions = localRows.Select(e => ParseLocalRegion(e, localMapId.ToString(), floorKey, profile)).ToArray(),
            NativeWalls = root.TryGetProperty("nativeWallSegments", out var nativeWalls)
                ? ParseNativeWalls(nativeWalls, asset.SourceWidth, asset.SourceHeight) : [],
            SourceEntries = entries, Coverage = coverage,
            LazyCleanMask = Mask(asset.CleanMaskFileName), LazyKnownBoundary = Mask(asset.KnownBoundaryFileName),
            LazyUnknownMask = Mask(asset.UnknownMaskFileName), LazyAuthorValidationMask = Mask(asset.AuthorValidationMaskFileName)
        };
    }

    private static Mat ReadMask(string path, MapEntryIdentityAsset asset)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024)
            throw new InvalidDataException("Entrance mask is too large.");
        var mask = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);
        try
        {
            if (mask.Empty() || mask.Type() != MatType.CV_8UC1
                || mask.Width != asset.SourceWidth || mask.Height != asset.SourceHeight)
                throw new InvalidDataException("Entrance binary channel has the wrong dimensions or type.");
            // PNG channels are 0/255 for portability. Do not silently threshold a grayscale image.
            using var nonBinary = new Mat();
            Cv2.InRange(mask, new Scalar(1), new Scalar(254), nonBinary);
            if (Cv2.CountNonZero(nonBinary) != 0)
                throw new InvalidDataException("Entrance channel is not a binary 0/255 PNG.");
            Cv2.Threshold(mask, mask, 0, 1, ThresholdTypes.Binary);
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    private static string SafePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(['/', '\\', ':']) >= 0 || Path.GetFileName(name) != name)
            throw new InvalidDataException("Entrance resource requires a map-local file name.");
        return Path.Combine(directory, name);
    }
}
