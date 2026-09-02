namespace IDVBuff.Features.Maps;

internal static class MapLearningSampleRules
{
    internal static IReadOnlyList<MapLearningSampleManifest> LatestPerMatch(
        IReadOnlyList<MapLearningSampleManifest> samples) => samples
        .GroupBy(item => item.MatchId)
        .Select(group => group
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.SampleId, StringComparer.Ordinal)
            .First())
        .OrderBy(item => item.CreatedAt)
        .ToArray();

    internal static bool IsSpatialSample(MapLearningSampleManifest sample) =>
        sample.SchemaVersion >= 2
        && sample.Candidates.Count >= 2
        && sample.Candidates.All(candidate => string.Equals(
            candidate.ReferenceScope, "floor", StringComparison.Ordinal))
        && sample.Candidates.Any(candidate =>
            candidate.MapId == sample.SelectedMapId && candidate.IsPositive);
}
