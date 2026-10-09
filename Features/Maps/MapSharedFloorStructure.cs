using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Identifies a reusable floor reference. Presentation edits do not revise it.
/// Raster dimensions and integrity metadata remain on FloorDefinition.
/// </summary>
public sealed class MapSharedFloorStructure
{
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public int Revision { get; set; } = 1;
    [JsonRequired] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public MapTilemapFloorSource? Source { get; set; }

    public MapSharedFloorStructure Clone() => new()
    {
        Id = Id,
        Revision = Revision,
        UpdatedAt = UpdatedAt,
        Source = Source?.Clone()
    };
}

public static class MapStructureRevisionRules
{
    internal static string GetRecognitionInputs(FloorRecognitionProfile profile) =>
        JsonSerializer.Serialize(new
        {
            profile.OrientationDegrees, profile.RecognitionRegion, profile.FreeCropPoints,
            profile.RecognitionPixelWidth, profile.RecognitionPixelHeight, profile.ValidMapBounds,
            profile.Anchors, profile.WholeImageIgnoreRegions, profile.BackgroundLayers
        });

    public static DateTimeOffset GetFloorUpdatedAt(MapRecord map, string floorKey) =>
        map.Floors.FirstOrDefault(floor => string.Equals(
            floor.Key, floorKey, StringComparison.OrdinalIgnoreCase))?.SharedStructure?.UpdatedAt
        ?? map.UpdatedAt;
}
