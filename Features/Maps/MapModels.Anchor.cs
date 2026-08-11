using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace IDVBuff.Features.Maps;

public enum RecognitionAnchorRole
{
    Required,
    Optional
}

public enum MapAnnotationType
{
    Text = 1,
    Outline = 2,
    Line = 3
}

/// <summary>Canonical RGB colors used by map annotations.</summary>
public static class MapAnnotationColor
{
    private static readonly string[] LegacyColors =
    [
        "#FF3B30",
        "#FF9500",
        "#FFCC00",
        "#34C759",
        "#32ADE6",
        "#007AFF",
        "#AF52DE",
        "#FF2D55",
        "#F2F2F2"
    ];

    public const string Default = "#007AFF";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 7 || value[0] != '#')
            return false;
        for (var index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
                return false;
        }
        normalized = value.ToUpperInvariant();
        return true;
    }

    public static string FromLegacyIndex(int colorIndex) =>
        LegacyColors[Math.Clamp(colorIndex, 0, LegacyColors.Length - 1)];

    public static int ToLegacyIndex(string? colorHex)
    {
        if (!TryNormalize(colorHex, out var normalized))
            return 5;
        var red = int.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = int.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = int.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var bestIndex = 0;
        var bestDistance = long.MaxValue;
        for (var index = 0; index < LegacyColors.Length; index++)
        {
            var candidate = LegacyColors[index];
            var deltaRed = red - int.Parse(candidate.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var deltaGreen = green - int.Parse(candidate.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var deltaBlue = blue - int.Parse(candidate.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var distance = (long)deltaRed * deltaRed
                + (long)deltaGreen * deltaGreen
                + (long)deltaBlue * deltaBlue;
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            bestIndex = index;
        }
        return bestIndex;
    }
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

/// <summary>A user-placed text, outline, or directed line on a map floor.</summary>
public sealed class MapAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MapAnnotationType Type { get; set; }
    /// <summary>Legacy nine-color palette index retained for old catalogs and packages.</summary>
    public int ColorIndex { get; set; }
    /// <summary>Canonical opaque RGB value in #RRGGBB form.</summary>
    public string? ColorHex { get; set; }
    public NormalizedRectangle? Bounds { get; set; }
    public NormalizedPoint? Start { get; set; }
    public NormalizedPoint? End { get; set; }
    public string? Text { get; set; }
    /// <summary>Optional requested font family for text annotations; null keeps legacy rendering.</summary>
    public string? FontFamily { get; set; }
    /// <summary>Optional font size in DIPs on a 1280-pixel-wide map reference.</summary>
    public double? FontSize { get; set; }
    public bool? IsBold { get; set; }
    public bool? IsItalic { get; set; }
    public bool? IsStrikethrough { get; set; }

    [JsonIgnore]
    public string EffectiveColorHex => MapAnnotationColor.TryNormalize(ColorHex, out var normalized)
        ? normalized
        : MapAnnotationColor.FromLegacyIndex(ColorIndex);

    [JsonIgnore]
    public bool IsValid => HasValidColor && Type switch
    {
        MapAnnotationType.Text or MapAnnotationType.Outline => Bounds?.IsValid is true,
        MapAnnotationType.Line => IsValidLine(Start, End),
        _ => false
    };

    [JsonIgnore]
    private bool HasValidColor => MapAnnotationColor.TryNormalize(ColorHex, out _)
        || ColorIndex is >= 0 and <= 8;

    public MapAnnotation Clone() => new()
    {
        Id = Id,
        Type = Type,
        ColorIndex = ColorIndex,
        ColorHex = ColorHex,
        Bounds = Bounds?.Clone(),
        Start = Start?.Clone(),
        End = End?.Clone(),
        Text = Text,
        FontFamily = FontFamily,
        FontSize = FontSize,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsStrikethrough = IsStrikethrough
    };

    private static bool IsValidLine(NormalizedPoint? start, NormalizedPoint? end)
    {
        if (start?.IsValid is not true || end?.IsValid is not true)
            return false;
        const double epsilon = 0.000001d;
        return Math.Abs(start.X - end.X) > epsilon
            || Math.Abs(start.Y - end.Y) > epsilon;
    }
}
