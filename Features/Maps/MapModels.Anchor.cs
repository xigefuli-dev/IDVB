using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

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
