using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>A rectangle expressed as a fraction of the original image dimensions.</summary>
public sealed class NormalizedRectangle
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    [JsonIgnore]
    public bool IsValid => Width >= 0.01 && Height >= 0.01;

    public NormalizedRectangle Clone() => new() { X = X, Y = Y, Width = Width, Height = Height };
}

/// <summary>A point expressed as a fraction of the original image dimensions.</summary>
public sealed class NormalizedPoint
{
    public double X { get; set; }
    public double Y { get; set; }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(X)
        && double.IsFinite(Y)
        && X is >= 0d and <= 1d
        && Y is >= 0d and <= 1d;

    public NormalizedPoint Clone() => new() { X = X, Y = Y };
}

public enum MapFloor
{
    First = 1,
    Second = 2
}

/// <summary>V10: user-defined floor identifier and explicit local image bindings.</summary>
public sealed class FloorDefinition
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    /// <summary>Explicit image file binding inside the map's local data directory.</summary>
    public string ImageFileName { get; set; } = string.Empty;
    /// <summary>SHA-256 of the local source image, stored as lowercase hexadecimal.</summary>
    public string ImageSha256 { get; set; } = string.Empty;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public long ImageFileLength { get; set; }
    public long ImageLastWriteUtcTicks { get; set; }
    /// <summary>Explicit recognition-image file. For a full-image profile this equals ImageFileName.</summary>
    public string RecognitionFileName { get; set; } = string.Empty;
    /// <summary>SHA-256 of the generated recognition image.</summary>
    public string RecognitionSha256 { get; set; } = string.Empty;
    public string RecognitionSourceSha256 { get; set; } = string.Empty;
    public int RecognitionWidth { get; set; }
    public int RecognitionHeight { get; set; }
    public long RecognitionFileLength { get; set; }
    public long RecognitionLastWriteUtcTicks { get; set; }
    /// <summary>Explicit overlay-image file generated from the recognition image.</summary>
    public string OverlayFileName { get; set; } = string.Empty;
    /// <summary>SHA-256 of the generated overlay image.</summary>
    public string OverlaySha256 { get; set; } = string.Empty;
    public string OverlaySourceSha256 { get; set; } = string.Empty;
    public int OverlayWidth { get; set; }
    public int OverlayHeight { get; set; }
    public long OverlayFileLength { get; set; }
    public long OverlayLastWriteUtcTicks { get; set; }
    /// <summary>Optional fixed-width thumbnail generated for the map list.</summary>
    public string ThumbnailFileName { get; set; } = string.Empty;
    public string ThumbnailSha256 { get; set; } = string.Empty;
    public int ThumbnailWidth { get; set; }
    public int ThumbnailHeight { get; set; }
    public long ThumbnailFileLength { get; set; }
    public long ThumbnailLastWriteUtcTicks { get; set; }
}

/// <summary>
/// Resolves the runtime floor identity from the user-defined ordering.  Gate
/// capability belongs to the first ordered floor, never to a literal key.
/// </summary>
public static class MapFloorRules
{
    public static IReadOnlyList<FloorDefinition> GetOrderedFloors(MapRecord map) =>
        (map.Floors ?? [])
            .Where(floor => floor is not null && !string.IsNullOrWhiteSpace(floor.Key))
            .OrderBy(floor => floor.SortOrder)
            .ThenBy(floor => floor.Key, StringComparer.Ordinal)
            .ToArray();

    public static string GetPrimaryFloorKey(MapRecord map) =>
        GetOrderedFloors(map).FirstOrDefault()?.Key
        ?? map.Recognition?.FirstFloor?.FloorKey
        ?? "1f";

    public static FloorRecognitionProfile? GetFloorProfile(
        MapRecord map,
        string? floorKey)
    {
        if (string.IsNullOrWhiteSpace(floorKey))
            return null;

        var recognition = map.Recognition;
        if (recognition is null)
            return null;
        var profile = recognition.GetFloor(floorKey);
        if (profile is not null)
            return profile;

        if (string.Equals(
                recognition.FirstFloor?.FloorKey,
                floorKey,
                StringComparison.Ordinal))
        {
            return recognition.FirstFloor;
        }

        return string.Equals(
                recognition.SecondFloor?.FloorKey,
                floorKey,
                StringComparison.Ordinal)
            ? recognition.SecondFloor
            : null;
    }

    public static int GetFloorPosition(MapRecord map, string floorKey)
    {
        var ordered = GetOrderedFloors(map);
        for (var index = 0; index < ordered.Count; index++)
        {
            if (string.Equals(ordered[index].Key, floorKey, StringComparison.Ordinal))
                return index + 1;
        }

        return 0;
    }

    public static string? GetFloorKeyAtPosition(MapRecord map, int position) =>
        position > 0 && position <= GetOrderedFloors(map).Count
            ? GetOrderedFloors(map)[position - 1].Key
            : null;

    public static string GetFloorDisplayName(MapRecord map, string floorKey) =>
        GetOrderedFloors(map)
            .FirstOrDefault(floor => string.Equals(
                floor.Key,
                floorKey,
                StringComparison.Ordinal))
            ?.DisplayName
        ?? floorKey;

    /// <summary>
    /// Returns the next floor in the user-defined order.  Floor keys are
    /// intentionally not inferred from the legacy 1F/2F enum: imported maps
    /// may use any number of floors and any stable keys.
    /// </summary>
    public static string? GetNextFloorKey(MapRecord map, string? currentFloorKey)
    {
        var floors = GetOrderedFloors(map);
        if (floors.Count == 0)
            return null;

        var currentIndex = -1;
        if (!string.IsNullOrWhiteSpace(currentFloorKey))
        {
            currentIndex = floors
                .Select((floor, index) => (floor.Key, index))
                .FirstOrDefault(
                    pair => string.Equals(
                        pair.Key,
                        currentFloorKey,
                        StringComparison.OrdinalIgnoreCase),
                    (Key: string.Empty, index: -1))
                .index;
        }

        return floors[(currentIndex + 1) % floors.Count].Key;
    }

    public static bool UsesDoubleGateAlignment(MapRecord map, string floorKey) =>
        string.Equals(
            GetPrimaryFloorKey(map),
            floorKey,
            StringComparison.Ordinal);
}

public sealed class MapRecord
{
    public Guid Id { get; set; }
    public int SequenceNumber { get; set; }
    public string FloorOneFileName { get; set; } = string.Empty;
    public string FloorTwoFileName { get; set; } = string.Empty;
    public MapRecognitionProfile Recognition { get; set; } = new();
    public string Source { get; set; } = "manual";
    public Guid? SourceProjectId { get; set; }
    public long? SourceProjectRevision { get; set; }
    public string? SourceVisualSha256 { get; set; }
    public string? SourceStructureSha256 { get; set; }

    /// <summary>Portable title. Empty legacy values use the local sequence label.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Portable content revision; incremented whenever an existing map is saved.</summary>
    public int ContentVersion { get; set; } = 1;

    /// <summary>Gate attributes not currently editable by the map editor.</summary>
    public List<MapGateDefinition> PortableGates { get; set; } = [];

    // Compatibility fields for map packages created before recognition profiles existed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NormalizedRectangle? MainEntrance { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NormalizedRectangle? SideEntrance { get; set; }

    /// <summary>V6: map classification / season group. Defaults to "S1" for all existing maps.</summary>
    public string Class { get; set; } = "S1";

    /// <summary>V6: ordered list of floor definitions (key, display name, sort order).</summary>
    public List<FloorDefinition> Floors { get; set; } =
    [
        new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 },
        new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 }
    ];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? $"地图 {SequenceNumber}"
        : Title;

    public bool NeedsCanonicalFloorNormalization()
    {
        Recognition ??= new MapRecognitionProfile();
        Recognition.Floors ??= [];
        var orderedFloors = MapFloorRules.GetOrderedFloors(this);
        if (orderedFloors.Count != Recognition.Floors.Count)
            return true;

        var profiles = new List<FloorRecognitionProfile>();
        for (var index = 0; index < orderedFloors.Count; index++)
        {
            var floor = orderedFloors[index];
            var profile = Recognition.GetFloor(floor.Key);
            if (profile is null
                || !string.Equals(profile.FloorKey, floor.Key, StringComparison.Ordinal)
                || profiles.Any(previous => ReferenceEquals(previous, profile)))
            {
                return true;
            }

            if ((index == 0 && profile.Floor != MapFloor.First)
                || (index == 1 && profile.Floor != MapFloor.Second))
            {
                return true;
            }
            profiles.Add(profile);
        }

        if (orderedFloors.Count > 0
            && !string.Equals(
                Recognition.FirstFloor?.FloorKey,
                orderedFloors[0].Key,
                StringComparison.Ordinal))
        {
            return true;
        }

        return orderedFloors.Count > 1
            && !string.Equals(
                Recognition.SecondFloor?.FloorKey,
                orderedFloors[1].Key,
                StringComparison.Ordinal);
    }

    public void NormalizeRecognition()
    {
        Recognition ??= new MapRecognitionProfile();

        // V6 migration: populate Floors list and Class from legacy data if missing
        Floors ??= [];
        if (Floors.Count == 0)
        {
            Floors.Add(new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 });
            Floors.Add(new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 });
        }

        Recognition.NormalizeForFloors(MapFloorRules.GetOrderedFloors(this));
        var main = Recognition.FirstFloor.FindAnchor("main-entrance")!;
        var side = Recognition.FirstFloor.FindAnchor("side-entrance")!;
        if (!main.IsMarked && MainEntrance?.IsValid is true)
            main.Bounds = MainEntrance.Clone();
        if (!side.IsMarked && SideEntrance?.IsValid is true)
            side.Bounds = SideEntrance.Clone();
        // Legacy fields are read once for migration only; new packages keep a single recognition source of truth.
        MainEntrance = null;
        SideEntrance = null;

        if (string.IsNullOrWhiteSpace(Class))
            Class = "S1";
        if (ContentVersion <= 0)
            ContentVersion = 1;
        PortableGates ??= [];
    }

    public MapRecord Clone()
    {
        NormalizeRecognition();
        return new MapRecord
        {
            Id = Id,
            SequenceNumber = SequenceNumber,
            FloorOneFileName = FloorOneFileName,
            FloorTwoFileName = FloorTwoFileName,
            Recognition = Recognition.Clone(),
            Source = Source,
            SourceProjectId = SourceProjectId,
            SourceProjectRevision = SourceProjectRevision,
            SourceVisualSha256 = SourceVisualSha256,
            SourceStructureSha256 = SourceStructureSha256,
            Title = Title,
            ContentVersion = ContentVersion,
            PortableGates = PortableGates.Select(gate => gate.Clone()).ToList(),
            MainEntrance = MainEntrance?.Clone(),
            SideEntrance = SideEntrance?.Clone(),
            Class = Class,
            Floors = Floors.Select(f => new FloorDefinition
            {
                Key = f.Key,
                DisplayName = f.DisplayName,
                SortOrder = f.SortOrder,
                ImageFileName = f.ImageFileName,
                ImageSha256 = f.ImageSha256,
                ImageWidth = f.ImageWidth,
                ImageHeight = f.ImageHeight,
                ImageFileLength = f.ImageFileLength,
                ImageLastWriteUtcTicks = f.ImageLastWriteUtcTicks,
                RecognitionFileName = f.RecognitionFileName,
                RecognitionSha256 = f.RecognitionSha256,
                RecognitionSourceSha256 = f.RecognitionSourceSha256,
                RecognitionWidth = f.RecognitionWidth,
                RecognitionHeight = f.RecognitionHeight,
                RecognitionFileLength = f.RecognitionFileLength,
                RecognitionLastWriteUtcTicks = f.RecognitionLastWriteUtcTicks,
                OverlayFileName = f.OverlayFileName,
                OverlaySha256 = f.OverlaySha256,
                OverlaySourceSha256 = f.OverlaySourceSha256,
                OverlayWidth = f.OverlayWidth,
                OverlayHeight = f.OverlayHeight,
                OverlayFileLength = f.OverlayFileLength,
                OverlayLastWriteUtcTicks = f.OverlayLastWriteUtcTicks,
                ThumbnailFileName = f.ThumbnailFileName,
                ThumbnailSha256 = f.ThumbnailSha256,
                ThumbnailWidth = f.ThumbnailWidth,
                ThumbnailHeight = f.ThumbnailHeight,
                ThumbnailFileLength = f.ThumbnailFileLength,
                ThumbnailLastWriteUtcTicks = f.ThumbnailLastWriteUtcTicks
            }).ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}

public sealed class MapDraft
{
    public Guid? Id { get; set; }
    public string? FloorOnePath { get; set; }
    public string? FloorTwoPath { get; set; }
    /// <summary>V6: floor image source paths keyed by <see cref="FloorDefinition.Key"/>.</summary>
    public Dictionary<string, string> FloorPaths { get; set; } = [];
    /// <summary>Preview paths keyed by floor. Existing maps use the selected recognition region when available.</summary>
    public Dictionary<string, string> FloorPreviewPaths { get; set; } = [];
    public Dictionary<string, string> FloorRecognitionSourcePaths { get; set; } = [];
    /// <summary>V6: ordered floor definitions carrying key, display name, and sort order.</summary>
    public List<FloorDefinition> Floors { get; set; } = [];
    /// <summary>V6: map classification label.</summary>
    public string Class { get; set; } = "S1";
    public string Title { get; set; } = string.Empty;
    public int ContentVersion { get; set; } = 1;
    public string Source { get; set; } = "manual";
    public Guid? SourceProjectId { get; set; }
    public long? SourceProjectRevision { get; set; }
    public string? SourceVisualSha256 { get; set; }
    public string? SourceStructureSha256 { get; set; }
    public List<MapGateDefinition> PortableGates { get; set; } = [];
    internal bool CreateAsImportedCopy { get; set; }
    public MapRecognitionProfile Recognition { get; set; } = new();
    /// <summary>IDVM 导入时，各楼层侧门特征图的临时暂存路径（floorKey → 磁盘绝对路径）。</summary>
    internal Dictionary<string, string> SideEntranceFeaturePaths { get; set; } = [];
}

public sealed record MapImportClassDraft(
    string SourceName,
    IReadOnlyList<MapDraft> Maps);

public sealed record MapImportBatchResult(
    IReadOnlyList<string> CreatedClasses,
    IReadOnlyList<MapRecord> ImportedMaps);
