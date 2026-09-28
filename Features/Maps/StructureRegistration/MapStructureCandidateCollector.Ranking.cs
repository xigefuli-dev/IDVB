namespace IDVBuff.Features.Maps;

internal static partial class MapStructureCandidateCollector
{
    internal static IReadOnlyList<MapStructureCandidate> DistinctCandidates(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapOverlayTransform lockedTransform)
    {
        var distinct = new List<MapStructureCandidate>();
        foreach (var candidate in candidates
            .OrderBy(item => item.CompositeCost)
            .ThenBy(item => MapStructureEvaluator.Distance(
                item.OffsetX,
                item.OffsetY,
                lockedTransform.OffsetX,
                lockedTransform.OffsetY)))
        {
            var duplicate = distinct.Any(existing =>
                StructureRegistrationRules.IsSameAlignmentBasin(
                    existing,
                    candidate,
                    tuning));
            if (!duplicate)
                distinct.Add(candidate);
        }
        return distinct;
    }

    internal static (
        MapStructureCandidate[] Ordered,
        MapStructureCandidate[] Diagnostic,
        MapStructureCandidate[] Valid) RankCandidatesByValidity(
        IReadOnlyList<MapStructureCandidate> candidates,
        MapStructureRegistrationTuning tuning,
        MapOverlayTransform lockedTransform,
        bool restrictedSearch,
        MapStructureRegistrationRequest? request = null)
    {
        var ordered = DistinctCandidates(candidates, tuning, lockedTransform)
            .OrderBy(candidate => candidate.CompositeCost)
            .ThenBy(candidate => MapStructureEvaluator.Distance(
                candidate.OffsetX,
                candidate.OffsetY,
                lockedTransform.OffsetX,
                lockedTransform.OffsetY))
            .ToArray();
        var diagnostic = ordered
            .Take(tuning.TopCandidateCount)
            .ToArray();
        // If the raw optimum of a neutral cold scale search lies at the
        // sampled boundary, the whole scale estimate is censored. Selecting
        // the next interior candidate would merely turn 1.70 into 1.60 and
        // still seed the session from an unclosed search curve.
        var rawBestRejection = ordered.Length == 0
            ? MapStructureRejectionReason.NoCandidate
            : MapStructureValidator.ValidateAbsolute(
                ordered[0],
                tuning,
                restrictedSearch,
                request);
        // Identity boundaries can distinguish adjacent pixels in one basin.
        // Discard invalid poses before basin suppression in that scoped scan.
        var validityOrdered = request is not null
            && ScanExecutionContext.Current?.HasAlignmentConstraint(request) == true
            ? DistinctCandidates(candidates.Where(candidate => MapStructureValidator.ValidateAbsolute(
                candidate, tuning, restrictedSearch, request) == MapStructureRejectionReason.None).ToArray(),
                tuning, lockedTransform).OrderBy(candidate => candidate.CompositeCost).ToArray()
            : ordered;
        var valid = request?.ForceBestCandidate == true
            ? ordered
                .OrderByDescending(candidate => candidate.FromAppearanceSearch)
                .ThenBy(candidate => ScalePriority(candidate, request.LowStructurePlan))
                .ThenByDescending(candidate => candidate.AppearanceCorrelation)
                .ThenBy(candidate => candidate.CompositeCost)
                .Take(tuning.TopCandidateCount)
                .ToArray()
            : rawBestRejection == MapStructureRejectionReason.ScaleSearchBoundary
            ? []
            : validityOrdered
                .Where(candidate => MapStructureValidator.ValidateAbsolute(
                    candidate,
                    tuning,
                    restrictedSearch,
                    request) == MapStructureRejectionReason.None)
                .Take(tuning.TopCandidateCount)
                .ToArray();

        if (tuning.Channel == MapAlignmentChannel.LowStructure)
            LogLowStructureCandidateSelection(candidates, ordered, diagnostic, valid);

        static int ScalePriority(
            MapStructureCandidate candidate,
            LowStructureAlignmentPlan? plan)
        {
            if (plan is null || plan.Scales.Count == 0)
                return int.MaxValue;
            return plan.Scales
                .Select((scale, index) => new
                {
                    Index = index,
                    Distance = Math.Abs(scale - candidate.Scale)
                })
                .MinBy(item => item.Distance)!.Index;
        }

        return (ordered, diagnostic, valid);
    }
}
