namespace IDVBuff.Features.Maps;

// Compatibility name for low-structure callers and existing diagnostics.
internal static class MapStructureLowScaleSelector
{
    internal static IReadOnlyList<double> Rank(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        MapStructureRegistrationTuning tuning) =>
        MapStructureScaleEstimator.Rank(live, reference, tuning);

    internal static LowStructureScaleSelection Analyze(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        MapStructureRegistrationTuning tuning,
        bool includeAppearanceScale = true,
        double? preferredScale = null,
        bool useScaleHint = false) =>
        MapStructureScaleEstimator.Analyze(
            live, reference, tuning, includeAppearanceScale, preferredScale, useScaleHint);

    internal static IReadOnlyList<double> SelectExactCandidates(
        double refinedWinner,
        IReadOnlyList<double> refinedGrid,
        IReadOnlyList<double> orderedCoarseBasins,
        bool ambiguous) =>
        MapStructureScaleEstimator.SelectExactCandidates(
            refinedWinner, refinedGrid, orderedCoarseBasins, ambiguous);

    internal static IReadOnlyList<double> BuildFineScaleGrid(
        IReadOnlyList<double> coarseGrid,
        int winnerIndex,
        double maximumRelativeStep) =>
        MapStructureScaleEstimator.BuildFineScaleGrid(
            coarseGrid, winnerIndex, maximumRelativeStep);
}
