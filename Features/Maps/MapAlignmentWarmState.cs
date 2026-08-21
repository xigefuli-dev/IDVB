namespace IDVBuff.Features.Maps;

/// <summary>Class chosen once for a map-open operation.</summary>
internal enum MapAlignmentExecutionClass
{
    Initial,
    Steady
}

/// <summary>
/// The complete capture and reference identity used by a reliable alignment.
/// Widths are intentionally part of the key: a transform from another capture
/// geometry must never become a local-search seed.
/// </summary>
internal readonly record struct MapAlignmentContextKey(
    Guid MatchId,
    Guid MapId,
    DateTimeOffset MapUpdatedAt,
    string FloorKey,
    int ClientWidth,
    int ClientHeight,
    int ViewportWidth,
    int ViewportHeight,
    string StructureGeneration)
{
    public MapAlignmentContextKey Normalize() => this with
    {
        FloorKey = NormalizeFloor(FloorKey),
        StructureGeneration = StructureGeneration?.Trim() ?? string.Empty
    };

    private static string NormalizeFloor(string? floor) =>
        string.IsNullOrWhiteSpace(floor) ? string.Empty : floor.Trim().ToLowerInvariant();

    public override string ToString() =>
        $"match={MatchId:D};map={MapId:D};updated={MapUpdatedAt:O};floor={FloorKey};"
        + $"client={ClientWidth}x{ClientHeight};viewport={ViewportWidth}x{ViewportHeight};"
        + $"generation={StructureGeneration}";
}

/// <summary>Validated transform evidence retained for the current match.</summary>
internal sealed class WarmAlignmentState
{
    public required MapAlignmentContextKey ContextKey { get; init; }
    public required MapAlignmentSession Session { get; set; }
    public required MapSimilarityTransform LastTransform { get; set; }
    public double Confidence { get; set; }
    public double CandidateMargin { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; }
    public int SuccessCount { get; set; }
    public List<MapSimilarityTransform> RecentTransforms { get; } = [];
}
