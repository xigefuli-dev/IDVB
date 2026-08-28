namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    internal static bool ShouldStopLowStructureSearch(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrationRequest request)
    {
        var ranking = MapStructureCandidateCollector.RankCandidatesByValidity(
            candidates,
            tuning,
            request.LockedTransform,
            request.RestrictSearchToLockedTransform,
            request);
        var valid = ranking.Valid;
        if (valid.Length == 0)
            return false;

        var best = valid[0];
        var second = valid.Skip(1).FirstOrDefault(candidate =>
            MapStructureEvaluator.Distance(
                candidate.OffsetX,
                candidate.OffsetY,
                best.OffsetX,
                best.OffsetY)
            >= Math.Max(4d, tuning.CandidateDuplicateRadius * 4d));
        var secondScore = second?.CompositeCost ?? double.PositiveInfinity;
        var margin = double.IsPositiveInfinity(secondScore)
            ? 1d
            : Math.Clamp(
                (secondScore - best.CompositeCost)
                / Math.Max(tuning.MarginNormalizationFloor, secondScore),
                0d,
                1d);
        var requiredMargin = tuning.MinimumCandidateMargin
            * (best.UsedGlobalSearch
                ? tuning.GlobalSearchMarginMultiplier
                : 1d);
        return MapStructureValidator.Validate(
            best,
            margin,
            requiredMargin,
            tuning,
            request.RestrictSearchToLockedTransform,
            request)
            == MapStructureRejectionReason.None;
    }
}
