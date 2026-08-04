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

public enum RecognitionAnchorRole
{
    Required,
    Optional
}

public enum MapAnnotationType
{
    Text = 1,
    Outline = 2
}

/// <summary>
/// Portable semantic gate metadata. The editor manipulates the built-in gate
/// anchors; IDVM export synchronizes their bounds into these records.
/// </summary>
public sealed class MapGateDefinition
{
    public string Id { get; set; } = string.Empty;
    public string FloorKey { get; set; } = string.Empty;
    public string Role { get; set; } = "unknown";
    public NormalizedRectangle Bounds { get; set; } = new();
    public double DirectionDegrees { get; set; }
    public bool Enabled { get; set; } = true;
    public double Confidence { get; set; } = 1d;

    public MapGateDefinition Clone() => new()
    {
        Id = Id,
        FloorKey = FloorKey,
        Role = Role,
        Bounds = Bounds.Clone(),
        DirectionDegrees = DirectionDegrees,
        Enabled = Enabled,
        Confidence = Confidence
    };
}

/// <summary>A visual region that later CV matching will use as evidence for a map floor.</summary>
public sealed class RecognitionAnchor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public RecognitionAnchorRole Role { get; set; }
    /// <summary>Relative CV importance. Built-in required anchors default to 1; optional anchors default to 0.35.</summary>
    public double Weight { get; set; }
    public NormalizedRectangle? Bounds { get; set; }
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public bool IsMarked => Bounds?.IsValid is true;

    public RecognitionAnchor Clone() => new()
    {
        Id = Id,
        Key = Key,
        DisplayName = DisplayName,
        Role = Role,
        Weight = Weight,
        Bounds = Bounds?.Clone(),
        IsBuiltIn = IsBuiltIn
    };
}

/// <summary>A user-placed annotation marker (text label or outline box) on a map floor.</summary>
public sealed class MapAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MapAnnotationType Type { get; set; }
    public int ColorIndex { get; set; }
    public NormalizedRectangle Bounds { get; set; } = new();
    public string? Text { get; set; }

    [JsonIgnore]
    public bool IsValid => Bounds.IsValid && ColorIndex is >= 0 and <= 8;

    public MapAnnotation Clone() => new()
    {
        Id = Id,
        Type = Type,
        ColorIndex = ColorIndex,
        Bounds = Bounds.Clone(),
        Text = Text
    };
}

public sealed class FloorRecognitionProfile
{
    public MapFloor Floor { get; set; }

    /// <summary>V6: string-based floor key matching <see cref="FloorDefinition.Key"/>.</summary>
    public string FloorKey { get; set; } = string.Empty;

    /// <summary>Reserved for later strategy/configuration UI. Valid values are 0, 90, 180, and 270.</summary>
    public int OrientationDegrees { get; set; }
    /// <summary>
    /// The non-destructive recognition crop, expressed relative to the original source image.
    /// A missing value means that the region has not been explicitly configured.
    /// </summary>
    public NormalizedRectangle? RecognitionRegion { get; set; }
    /// <summary>Pixel dimensions of the generated recognition image.</summary>
    public int RecognitionPixelWidth { get; set; }
    public int RecognitionPixelHeight { get; set; }
    /// <summary>
    /// The valid full-map area in recognition-image pixels. A missing value
    /// means the complete recognition image and is the schema-v3 migration
    /// default.
    /// </summary>
    public MapReferenceBounds? ValidMapBounds { get; set; }
    public List<RecognitionAnchor> Anchors { get; set; } = [];
    public List<NormalizedRectangle> WholeImageIgnoreRegions { get; set; } = [];
    public List<MapAnnotation> Annotations { get; set; } = [];

    // ── 侧门特征（由 SideEntranceFeaturePreprocessor 生成） ──────────
    /// <summary>侧门特征图文件名（相对 map 目录）。为空表示尚未预处理。</summary>
    public string SideEntranceFeatureFileName { get; set; } = string.Empty;
    /// <summary>特征图 SHA-256（小写十六进制）。</summary>
    public string SideEntranceFeatureSha256 { get; set; } = string.Empty;
    /// <summary>生成特征图时所用识别图的 SHA-256，用于失效检测。</summary>
    public string SideEntranceFeatureSourceSha256 { get; set; } = string.Empty;
    /// <summary>实际中心点 X（识别图像素坐标，边界挤压后）。</summary>
    public double SideEntranceFeatureCenterX { get; set; }
    /// <summary>实际中心点 Y（识别图像素坐标，边界挤压后）。</summary>
    public double SideEntranceFeatureCenterY { get; set; }
    /// <summary>实际半径（预处理时生效的像素值）。</summary>
    public int SideEntranceFeatureRadius { get; set; }

    public RecognitionAnchor? FindAnchor(string key) =>
        Anchors.FirstOrDefault(anchor => string.Equals(anchor.Key, key, StringComparison.Ordinal));

    public RecognitionAnchor? FindAnchor(Guid id) =>
        Anchors.FirstOrDefault(anchor => anchor.Id == id);

    [JsonIgnore]
    public IEnumerable<RecognitionAnchor> RequiredAnchors => Anchors.Where(anchor => anchor.Role == RecognitionAnchorRole.Required);

    public NormalizedRectangle GetEffectiveRecognitionRegion() =>
        RecognitionRegion?.IsValid is true
            ? RecognitionRegion.Clone()
            : new NormalizedRectangle { Width = 1d, Height = 1d };

    public MapReferenceBounds GetEffectiveValidMapBounds() =>
        ValidMapBounds?.IsValid is true
            ? ValidMapBounds.Clone()
            : MapReferenceBounds.FullImage(
                RecognitionPixelWidth,
                RecognitionPixelHeight);

    public FloorRecognitionProfile Clone() => new()
    {
        Floor = Floor,
        FloorKey = FloorKey,
        OrientationDegrees = OrientationDegrees,
        RecognitionRegion = RecognitionRegion?.Clone(),
        RecognitionPixelWidth = RecognitionPixelWidth,
        RecognitionPixelHeight = RecognitionPixelHeight,
        ValidMapBounds = ValidMapBounds?.Clone(),
        Anchors = Anchors.Select(anchor => anchor.Clone()).ToList(),
        WholeImageIgnoreRegions = WholeImageIgnoreRegions.Select(region => region.Clone()).ToList(),
        Annotations = Annotations.Select(a => a.Clone()).ToList(),
        SideEntranceFeatureFileName = SideEntranceFeatureFileName,
        SideEntranceFeatureSha256 = SideEntranceFeatureSha256,
        SideEntranceFeatureSourceSha256 = SideEntranceFeatureSourceSha256,
        SideEntranceFeatureCenterX = SideEntranceFeatureCenterX,
        SideEntranceFeatureCenterY = SideEntranceFeatureCenterY,
        SideEntranceFeatureRadius = SideEntranceFeatureRadius
    };
}

/// <summary>Configuration for an optional global-image CV signal. It remains disabled until a later status-page switch enables it.</summary>
public sealed class WholeImageRecognitionSettings
{
    public bool IsEnabled { get; set; }
    public double Weight { get; set; } = 0.15;
    public double AnnotatedReferencePenalty { get; set; } = 0.55;
    public bool ReferenceMayContainAnnotations { get; set; } = true;

    public WholeImageRecognitionSettings Clone() => new()
    {
        IsEnabled = IsEnabled,
        Weight = Weight,
        AnnotatedReferencePenalty = AnnotatedReferencePenalty,
        ReferenceMayContainAnnotations = ReferenceMayContainAnnotations
    };
}

/// <summary>Versioned recognition data kept alongside the map's two image files.</summary>
[JsonConverter(typeof(MapRecognitionProfileJsonConverter))]
public sealed class MapRecognitionProfile
{
    public int SchemaVersion { get; set; } = 6;
    public FloorRecognitionProfile FirstFloor { get; set; } = new() { Floor = MapFloor.First };
    public FloorRecognitionProfile SecondFloor { get; set; } = new() { Floor = MapFloor.Second };
    public WholeImageRecognitionSettings WholeImage { get; set; } = new();

    /// <summary>V6: all floor profiles keyed by <see cref="FloorDefinition.Key"/>.</summary>
    public Dictionary<string, FloorRecognitionProfile> Floors { get; set; } = [];

    public FloorRecognitionProfile GetFloor(MapFloor floor) => floor == MapFloor.First ? FirstFloor : SecondFloor;

    /// <summary>V6: lookup a floor profile by string key.</summary>
    public FloorRecognitionProfile? GetFloor(string floorKey) =>
        Floors.TryGetValue(floorKey, out var profile) ? profile : null;

    public void EnsureStandardAnchors()
    {
        SchemaVersion = Math.Max(SchemaVersion, 6);
        FirstFloor ??= new FloorRecognitionProfile { Floor = MapFloor.First };
        SecondFloor ??= new FloorRecognitionProfile { Floor = MapFloor.Second };
        WholeImage ??= new WholeImageRecognitionSettings();
        FirstFloor.Floor = MapFloor.First;
        SecondFloor.Floor = MapFloor.Second;
        // Keep custom keys assigned by the V6 editor. Empty legacy profiles
        // still receive the traditional keys for backwards compatibility.
        FirstFloor.FloorKey = string.IsNullOrWhiteSpace(FirstFloor.FloorKey)
            ? "1f"
            : FirstFloor.FloorKey;
        SecondFloor.FloorKey = string.IsNullOrWhiteSpace(SecondFloor.FloorKey)
            ? "2f"
            : SecondFloor.FloorKey;
        FirstFloor.Anchors ??= [];
        SecondFloor.Anchors ??= [];
        FirstFloor.WholeImageIgnoreRegions ??= [];
        SecondFloor.WholeImageIgnoreRegions ??= [];
        FirstFloor.Annotations ??= [];
        SecondFloor.Annotations ??= [];
        foreach (var a in FirstFloor.Annotations.Concat(SecondFloor.Annotations))
            a.ColorIndex = Math.Clamp(a.ColorIndex, 0, 8);
        FirstFloor.OrientationDegrees = NormalizeOrientation(FirstFloor.OrientationDegrees);
        SecondFloor.OrientationDegrees = NormalizeOrientation(SecondFloor.OrientationDegrees);
        FirstFloor.RecognitionRegion = NormalizeRecognitionRegion(FirstFloor.RecognitionRegion);
        SecondFloor.RecognitionRegion = NormalizeRecognitionRegion(SecondFloor.RecognitionRegion);
        EnsureAnchor(FirstFloor, "main-entrance", "大门", RecognitionAnchorRole.Required, isBuiltIn: true);
        EnsureAnchor(FirstFloor, "side-entrance", "侧门", RecognitionAnchorRole.Required, isBuiltIn: true);
        EnsureAnchor(SecondFloor, "second-floor-primary", "二楼主锚点", RecognitionAnchorRole.Optional, isBuiltIn: true);
        ConfigureBuiltInAnchor(FirstFloor, "main-entrance", "大门", RecognitionAnchorRole.Required);
        ConfigureBuiltInAnchor(FirstFloor, "side-entrance", "侧门", RecognitionAnchorRole.Required);
        ConfigureBuiltInAnchor(SecondFloor, "second-floor-primary", "二楼主锚点", RecognitionAnchorRole.Optional);
        FirstFloor.RecognitionPixelWidth = Math.Max(0, FirstFloor.RecognitionPixelWidth);
        FirstFloor.RecognitionPixelHeight = Math.Max(0, FirstFloor.RecognitionPixelHeight);
        SecondFloor.RecognitionPixelWidth = Math.Max(0, SecondFloor.RecognitionPixelWidth);
        SecondFloor.RecognitionPixelHeight = Math.Max(0, SecondFloor.RecognitionPixelHeight);
        FirstFloor.ValidMapBounds = NormalizeValidMapBounds(FirstFloor);
        SecondFloor.ValidMapBounds = NormalizeValidMapBounds(SecondFloor);
        NormalizeAnchorWeights(FirstFloor);
        NormalizeAnchorWeights(SecondFloor);

        // V6: keep Floors dictionary in sync with FirstFloor / SecondFloor
        Floors[FirstFloor.FloorKey] = FirstFloor;
        Floors[SecondFloor.FloorKey] = SecondFloor;
    }

    public bool HasRequiredIdentificationData()
    {
        // V6: check all floors in Floors dictionary first
        foreach (var (_, floor) in Floors)
        {
            var required = floor.RequiredAnchors.ToArray();
            if (required.Length > 0
                && required.All(a => a.IsMarked))
                return true;
        }
        // Legacy fallback: check FirstFloor for main-entrance + side-entrance
        var main = FirstFloor.FindAnchor("main-entrance");
        var side = FirstFloor.FindAnchor("side-entrance");
        return main?.IsMarked is true
            && side?.IsMarked is true;
    }

    /// <summary>
    /// The editor's save boundary: the first floor only needs its main and
    /// side entrance markers. Recognition region and all other floors are
    /// optional editor data; an omitted region uses the full source image.
    /// </summary>
    public bool HasFirstFloorGateMarkers()
    {
        var main = FirstFloor.FindAnchor("main-entrance");
        var side = FirstFloor.FindAnchor("side-entrance");
        return main?.IsMarked is true && side?.IsMarked is true;
    }

    public bool HasGateMarkers(string floorKey)
    {
        var profile = GetFloor(floorKey);
        if (profile is null)
            return false;
        var main = profile.FindAnchor("main-entrance");
        var side = profile.FindAnchor("side-entrance");
        return main?.IsMarked is true && side?.IsMarked is true;
    }

    public bool HasAllRequiredAnchors() => HasRequiredIdentificationData();

    public MapRecognitionProfile Clone()
    {
        EnsureStandardAnchors();
        return new MapRecognitionProfile
        {
            SchemaVersion = SchemaVersion,
            FirstFloor = FirstFloor.Clone(),
            SecondFloor = SecondFloor.Clone(),
            Floors = Floors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone()),
            WholeImage = WholeImage.Clone()
        };
    }

    private static void EnsureAnchor(
        FloorRecognitionProfile floor,
        string key,
        string name,
        RecognitionAnchorRole role,
        bool isBuiltIn)
    {
        if (floor.FindAnchor(key) is not null)
            return;
        floor.Anchors.Add(new RecognitionAnchor
        {
            Key = key,
            DisplayName = name,
            Role = role,
            Weight = role == RecognitionAnchorRole.Required ? 1d : 0.35d,
            IsBuiltIn = isBuiltIn
        });
    }

    private static void ConfigureBuiltInAnchor(
        FloorRecognitionProfile floor,
        string key,
        string name,
        RecognitionAnchorRole role)
    {
        var anchor = floor.FindAnchor(key);
        if (anchor is null)
            return;
        anchor.DisplayName = name;
        anchor.Role = role;
        anchor.IsBuiltIn = true;
        anchor.Weight = role == RecognitionAnchorRole.Required ? 1d : 0.35d;
    }

    private static void NormalizeAnchorWeights(FloorRecognitionProfile floor)
    {
        foreach (var anchor in floor.Anchors)
        {
            if (anchor.Weight <= 0 || double.IsNaN(anchor.Weight) || double.IsInfinity(anchor.Weight))
                anchor.Weight = anchor.Role == RecognitionAnchorRole.Required ? 1d : 0.35d;
            else
                anchor.Weight = Math.Clamp(anchor.Weight, 0.05d, 2d);
        }
    }

    private static int NormalizeOrientation(int degrees) => degrees switch
    {
        0 or 90 or 180 or 270 => degrees,
        _ => 0
    };

    private static NormalizedRectangle? NormalizeRecognitionRegion(NormalizedRectangle? region)
    {
        if (region?.IsValid is not true)
            return null;
        var left = Math.Clamp(region.X, 0d, 1d);
        var top = Math.Clamp(region.Y, 0d, 1d);
        var right = Math.Clamp(region.X + region.Width, left, 1d);
        var bottom = Math.Clamp(region.Y + region.Height, top, 1d);
        var normalized = new NormalizedRectangle
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        return normalized.IsValid ? normalized : null;
    }

    private static MapReferenceBounds? NormalizeValidMapBounds(
        FloorRecognitionProfile profile)
    {
        if (profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }
        if (profile.ValidMapBounds?.IsValid is not true)
        {
            return MapReferenceBounds.FullImage(
                profile.RecognitionPixelWidth,
                profile.RecognitionPixelHeight);
        }

        var bounds = profile.ValidMapBounds;
        var left = Math.Clamp(bounds.X, 0d, profile.RecognitionPixelWidth);
        var top = Math.Clamp(bounds.Y, 0d, profile.RecognitionPixelHeight);
        var right = Math.Clamp(
            bounds.Right,
            left,
            profile.RecognitionPixelWidth);
        var bottom = Math.Clamp(
            bounds.Bottom,
            top,
            profile.RecognitionPixelHeight);
        var normalized = new MapReferenceBounds
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        return normalized.IsValid
            ? normalized
            : MapReferenceBounds.FullImage(
                profile.RecognitionPixelWidth,
                profile.RecognitionPixelHeight);
    }
}

public sealed class MapRecord
{
    public Guid Id { get; set; }
    public int SequenceNumber { get; set; }
    public string FloorOneFileName { get; set; } = string.Empty;
    public string FloorTwoFileName { get; set; } = string.Empty;
    public MapRecognitionProfile Recognition { get; set; } = new();

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

    public void NormalizeRecognition()
    {
        Recognition ??= new MapRecognitionProfile();
        Recognition.EnsureStandardAnchors();
        var main = Recognition.FirstFloor.FindAnchor("main-entrance")!;
        var side = Recognition.FirstFloor.FindAnchor("side-entrance")!;
        if (!main.IsMarked && MainEntrance?.IsValid is true)
            main.Bounds = MainEntrance.Clone();
        if (!side.IsMarked && SideEntrance?.IsValid is true)
            side.Bounds = SideEntrance.Clone();
        // Legacy fields are read once for migration only; new packages keep a single recognition source of truth.
        MainEntrance = null;
        SideEntrance = null;

        // V6 migration: populate Floors list and Class from legacy data if missing
        if (Floors.Count == 0)
        {
            Floors.Add(new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 });
            Floors.Add(new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 });
        }
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
    /// <summary>V6: ordered floor definitions carrying key, display name, and sort order.</summary>
    public List<FloorDefinition> Floors { get; set; } = [];
    /// <summary>V6: map classification label.</summary>
    public string Class { get; set; } = "S1";
    public string Title { get; set; } = string.Empty;
    public int ContentVersion { get; set; } = 1;
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

internal sealed class MapCatalogDocument
{
    /// <summary>Local catalog storage schema. Version 10 adds file stamps for fast validation.</summary>
    public int StorageSchemaVersion { get; set; }
    public int NextSequenceNumber { get; set; } = 1;
    /// <summary>
    /// Persisted independently of maps so an empty class remains available in the
    /// management UI. Display names are canonicalized by <see cref="MapRepository"/>.
    /// </summary>
    public List<string> Classes { get; set; } = ["S1"];
    public List<MapRecord> Maps { get; set; } = [];
}

/// <summary>
/// Keeps FirstFloor/SecondFloor readable from legacy catalogs while writing a
/// single canonical Floors dictionary in new catalogs.
/// </summary>
public sealed class MapRecognitionProfileJsonConverter : JsonConverter<MapRecognitionProfile>
{
    public override MapRecognitionProfile? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
            return new MapRecognitionProfile();

        var inner = CreateInnerOptions(options);
        var profile = new MapRecognitionProfile();
        if (root.TryGetProperty("SchemaVersion", out var schemaVersion)
            && schemaVersion.TryGetInt32(out var version))
            profile.SchemaVersion = version;
        if (root.TryGetProperty("WholeImage", out var wholeImage))
            profile.WholeImage = JsonSerializer.Deserialize<WholeImageRecognitionSettings>(
                wholeImage.GetRawText(), inner) ?? new WholeImageRecognitionSettings();

        if (root.TryGetProperty("FirstFloor", out var firstFloor))
            profile.FirstFloor = JsonSerializer.Deserialize<FloorRecognitionProfile>(
                firstFloor.GetRawText(), inner)
                ?? new FloorRecognitionProfile { Floor = MapFloor.First };
        if (root.TryGetProperty("SecondFloor", out var secondFloor))
            profile.SecondFloor = JsonSerializer.Deserialize<FloorRecognitionProfile>(
                secondFloor.GetRawText(), inner)
                ?? new FloorRecognitionProfile { Floor = MapFloor.Second };
        if (root.TryGetProperty("Floors", out var floors))
        {
            profile.Floors = JsonSerializer.Deserialize<Dictionary<string, FloorRecognitionProfile>>(
                floors.GetRawText(), inner) ?? [];
        }

        if (profile.Floors.Count > 0)
        {
            var ordered = profile.Floors.Values
                .OrderBy(floor => floor.Floor)
                .ThenBy(floor => floor.FloorKey, StringComparer.Ordinal)
                .ToArray();
            profile.FirstFloor = profile.Floors.TryGetValue("1f", out var first)
                ? first
                : ordered.FirstOrDefault(floor => floor.Floor == MapFloor.First)
                    ?? ordered.FirstOrDefault()
                    ?? profile.FirstFloor;
            profile.SecondFloor = profile.Floors.TryGetValue("2f", out var second)
                ? second
                : ordered.FirstOrDefault(floor => floor.Floor == MapFloor.Second)
                    ?? ordered.FirstOrDefault(floor => !ReferenceEquals(floor, profile.FirstFloor))
                    ?? profile.SecondFloor;
        }

        profile.EnsureStandardAnchors();
        return profile;
    }

    public override void Write(
        Utf8JsonWriter writer,
        MapRecognitionProfile value,
        JsonSerializerOptions options)
    {
        value.EnsureStandardAnchors();
        var inner = CreateInnerOptions(options);
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", Math.Max(6, value.SchemaVersion));
        writer.WritePropertyName("WholeImage");
        JsonSerializer.Serialize(writer, value.WholeImage, inner);
        writer.WritePropertyName("Floors");
        JsonSerializer.Serialize(writer, value.Floors, inner);
        writer.WriteEndObject();
    }

    private static JsonSerializerOptions CreateInnerOptions(JsonSerializerOptions options)
    {
        var inner = new JsonSerializerOptions(options);
        for (var index = inner.Converters.Count - 1; index >= 0; index--)
        {
            if (inner.Converters[index] is MapRecognitionProfileJsonConverter)
                inner.Converters.RemoveAt(index);
        }
        return inner;
    }
}

public sealed record MapCatalogSnapshot(
    IReadOnlyList<string> Classes,
    IReadOnlyList<MapRecord> Maps);

public sealed record MapClassDeletionResult(
    string ClassName,
    int DeletedMapCount);
