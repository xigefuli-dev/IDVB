using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

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
    /// <summary>生成侧门特征时使用的算法版本；不匹配时必须重建。</summary>
    public string SideEntranceFeatureAlgorithmVersion { get; set; } = string.Empty;
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
        SideEntranceFeatureAlgorithmVersion = SideEntranceFeatureAlgorithmVersion,
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
        SchemaVersion = Math.Max(SchemaVersion, 7);
        FirstFloor ??= new FloorRecognitionProfile { Floor = MapFloor.First };
        SecondFloor ??= new FloorRecognitionProfile { Floor = MapFloor.Second };
        WholeImage ??= new WholeImageRecognitionSettings();
        Floors ??= [];

        // A profile without a canonical dictionary is a legacy two-floor
        // profile. Keep both legacy entries independent while constructing
        // the canonical dictionary for it.
        if (Floors.Count == 0)
        {
            FirstFloor.Floor = MapFloor.First;
            SecondFloor = EnsureIndependentSecondFloor(FirstFloor, SecondFloor);
            SecondFloor.Floor = MapFloor.Second;
            FirstFloor.FloorKey = string.IsNullOrWhiteSpace(FirstFloor.FloorKey)
                ? "1f"
                : FirstFloor.FloorKey;
            SecondFloor.FloorKey = string.IsNullOrWhiteSpace(SecondFloor.FloorKey)
                || string.Equals(SecondFloor.FloorKey, FirstFloor.FloorKey, StringComparison.Ordinal)
                    ? "2f"
                    : SecondFloor.FloorKey;
            NormalizeFloorProfile(FirstFloor, MapFloor.First);
            NormalizeFloorProfile(SecondFloor, MapFloor.Second);
            Floors = new Dictionary<string, FloorRecognitionProfile>(StringComparer.Ordinal)
            {
                [FirstFloor.FloorKey] = FirstFloor,
                [SecondFloor.FloorKey] = SecondFloor
            };
            return;
        }

        // Once Floors exists it is the only canonical source. Compatibility
        // properties are projections and must never add phantom floors.
        NormalizeCanonicalDictionary();
        RefreshCompatibilityViews();
    }

    /// <summary>
    /// Reconciles recognition profiles with the map's ordered floor
    /// definitions. The floor definitions are authoritative for membership,
    /// order, and the first/second compatibility slots.
    /// </summary>
    public void NormalizeForFloors(IReadOnlyList<FloorDefinition> orderedFloors)
    {
        SchemaVersion = Math.Max(SchemaVersion, 7);
        FirstFloor ??= new FloorRecognitionProfile { Floor = MapFloor.First };
        SecondFloor ??= new FloorRecognitionProfile { Floor = MapFloor.Second };
        WholeImage ??= new WholeImageRecognitionSettings();
        Floors ??= [];

        var existing = Floors;
        var canonical = new Dictionary<string, FloorRecognitionProfile>(StringComparer.Ordinal);
        var usedProfiles = new List<FloorRecognitionProfile>();
        var floors = orderedFloors
            .Where(floor => floor is not null && !string.IsNullOrWhiteSpace(floor.Key))
            .ToArray();

        for (var index = 0; index < floors.Length; index++)
        {
            var floor = floors[index];
            existing.TryGetValue(floor.Key, out var candidate);
            if (candidate is null || usedProfiles.Any(previous => ReferenceEquals(previous, candidate)))
            {
                var legacyCandidate = index == 0 ? FirstFloor : index == 1 ? SecondFloor : null;
                if (candidate is null
                    && legacyCandidate is not null
                    && !usedProfiles.Any(previous => ReferenceEquals(previous, legacyCandidate)))
                {
                    candidate = legacyCandidate;
                }
                else
                {
                    candidate = candidate?.Clone() ?? new FloorRecognitionProfile();
                }
            }

            candidate.FloorKey = floor.Key;
            var compatibilityFloor = index switch
            {
                0 => MapFloor.First,
                1 => MapFloor.Second,
                _ => (MapFloor?)null
            };
            if (compatibilityFloor is { } enumFloor)
                candidate.Floor = enumFloor;
            NormalizeFloorProfile(candidate, compatibilityFloor);
            canonical[floor.Key] = candidate;
            usedProfiles.Add(candidate);
        }

        Floors = canonical;
        RefreshCompatibilityViews();
        if (floors.Length < 2)
        {
            SecondFloor = new FloorRecognitionProfile
            {
                Floor = MapFloor.Second,
                FloorKey = "2f"
            };
            NormalizeFloorProfile(SecondFloor, MapFloor.Second);
        }
    }

    private void NormalizeCanonicalDictionary()
    {
        var normalized = new Dictionary<string, FloorRecognitionProfile>(StringComparer.Ordinal);
        foreach (var (key, source) in Floors.ToArray())
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var profile = source ?? new FloorRecognitionProfile();
            profile.FloorKey = key;
            if (string.Equals(key, "1f", StringComparison.OrdinalIgnoreCase))
                profile.Floor = MapFloor.First;
            else if (string.Equals(key, "2f", StringComparison.OrdinalIgnoreCase))
                profile.Floor = MapFloor.Second;
            NormalizeFloorProfile(
                profile,
                profile.Floor is MapFloor.First or MapFloor.Second ? profile.Floor : null);
            normalized[key] = profile;
        }

        Floors = normalized;
    }

    private void RefreshCompatibilityViews()
    {
        var first = Floors.TryGetValue("1f", out var traditionalFirst)
            ? traditionalFirst
            : Floors.Values.FirstOrDefault(profile => profile.Floor == MapFloor.First)
                ?? Floors.Values.FirstOrDefault();
        var second = Floors.TryGetValue("2f", out var traditionalSecond)
            ? traditionalSecond
            : Floors.Values.FirstOrDefault(profile => profile.Floor == MapFloor.Second
                && !ReferenceEquals(profile, first))
                ?? Floors.Values.FirstOrDefault(profile => !ReferenceEquals(profile, first));

        FirstFloor = first ?? new FloorRecognitionProfile
        {
            Floor = MapFloor.First,
            FloorKey = "1f"
        };
        SecondFloor = EnsureIndependentSecondFloor(
            FirstFloor,
            second ?? new FloorRecognitionProfile
            {
                Floor = MapFloor.Second,
                FloorKey = "2f"
            });
        FirstFloor.Floor = MapFloor.First;
        SecondFloor.Floor = MapFloor.Second;
        NormalizeFloorProfile(FirstFloor, MapFloor.First);
        NormalizeFloorProfile(SecondFloor, MapFloor.Second);
    }

    private static FloorRecognitionProfile EnsureIndependentSecondFloor(
        FloorRecognitionProfile first,
        FloorRecognitionProfile second) =>
        ReferenceEquals(first, second)
            ? second.Clone()
            : second;

    private static void NormalizeFloorProfile(
        FloorRecognitionProfile profile,
        MapFloor? compatibilityFloor)
    {
        profile.Anchors ??= [];
        profile.WholeImageIgnoreRegions ??= [];
        profile.Annotations ??= [];
        NormalizeAnnotations(profile);
        profile.OrientationDegrees = NormalizeOrientation(profile.OrientationDegrees);
        profile.RecognitionRegion = NormalizeRecognitionRegion(profile.RecognitionRegion);
        profile.RecognitionPixelWidth = Math.Max(0, profile.RecognitionPixelWidth);
        profile.RecognitionPixelHeight = Math.Max(0, profile.RecognitionPixelHeight);
        profile.ValidMapBounds = NormalizeValidMapBounds(profile);
        NormalizeAnchorWeights(profile);

        if (compatibilityFloor == MapFloor.First)
        {
            EnsureAnchor(profile, "main-entrance", "大门", RecognitionAnchorRole.Required, isBuiltIn: true);
            EnsureAnchor(profile, "side-entrance", "侧门", RecognitionAnchorRole.Required, isBuiltIn: true);
            ConfigureBuiltInAnchor(profile, "main-entrance", "大门", RecognitionAnchorRole.Required);
            ConfigureBuiltInAnchor(profile, "side-entrance", "侧门", RecognitionAnchorRole.Required);
        }
        else if (compatibilityFloor == MapFloor.Second)
        {
            EnsureAnchor(profile, "second-floor-primary", "二楼主锚点", RecognitionAnchorRole.Optional, isBuiltIn: true);
            ConfigureBuiltInAnchor(profile, "second-floor-primary", "二楼主锚点", RecognitionAnchorRole.Optional);
        }
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
        var clone = new MapRecognitionProfile
        {
            SchemaVersion = SchemaVersion,
            Floors = Floors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone()),
            WholeImage = WholeImage.Clone()
        };
        clone.EnsureStandardAnchors();
        return clone;
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

    private static void NormalizeAnnotations(FloorRecognitionProfile floor)
    {
        floor.Annotations ??= [];
        foreach (var annotation in floor.Annotations)
        {
            if (!MapAnnotationColor.TryNormalize(annotation.ColorHex, out var colorHex))
                colorHex = MapAnnotationColor.FromLegacyIndex(annotation.ColorIndex);
            annotation.ColorHex = colorHex;
            annotation.ColorIndex = MapAnnotationColor.ToLegacyIndex(colorHex);
        }
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
        writer.WriteNumber("SchemaVersion", Math.Max(7, value.SchemaVersion));
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
