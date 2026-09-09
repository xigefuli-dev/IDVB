namespace IDVBuff.Features.Maps;

internal static partial class MapStructureCandidateCollector
{
    private static void LogLowStructureCandidateSelection(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureCandidate[] ordered,
        MapStructureCandidate[] diagnostic,
        MapStructureCandidate[] valid)
    {
        var bestChamfer = ordered.MinBy(candidate => candidate.ChamferPixels);
        var bestComposite = ordered.FirstOrDefault();
        var bestCoverage = ordered.MaxBy(candidate => candidate.EdgeCoverage);
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "LowStructureCandidateSelectionSummary",
            details: new()
            {
                // The coarse matcher applies peak suppression before full
                // evaluation, so these are not raw generated peaks.
                ["evaluatedCandidates"] = candidates.Count,
                ["afterNms"] = ordered.Length,
                ["afterTopK"] = diagnostic.Length,
                ["selectedAfterValidation"] = valid.Length,
                ["bestByChamfer"] = Describe(bestChamfer, ordered),
                ["bestByComposite"] = Describe(bestComposite, ordered),
                ["bestByEdgeCoverage"] = Describe(bestCoverage, ordered),
                ["selected"] = valid.Select(candidate => Describe(candidate, ordered)).ToArray(),
                ["bestChamferCandidatePruned"] = bestChamfer is not null && !diagnostic.Contains(bestChamfer),
                ["bestCoverageCandidatePruned"] = bestCoverage is not null && !diagnostic.Contains(bestCoverage)
            });
    }

    private static object? Describe(
        MapStructureCandidate? candidate,
        MapStructureCandidate[] ordered) => candidate is null
        ? null
        : new
        {
            rank = Array.IndexOf(ordered, candidate) + 1,
            candidate.Scale,
            candidate.ReferenceX,
            candidate.ReferenceY,
            candidate.CompositeCost,
            candidate.ChamferPixels,
            candidate.EdgeCoverage,
            candidate.OccupancyCoverage,
            candidate.ReferenceCoverage,
            candidate.ProjectionCorrelation,
            source = candidate.FromAppearanceSearch ? "AppearanceCandidate" : "DistancePeak"
        };
}
