using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureCandidateCollector
{
    internal static void SearchRestrictedCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        Point expected,
        Rect searchDomain,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        var current = MapStructureEvaluator.Evaluate(
            query,
            reference,
            referenceDistance,
            request,
            scale,
            Math.Clamp(expected.X, searchDomain.X, searchDomain.Right - 1),
            Math.Clamp(expected.Y, searchDomain.Y, searchDomain.Bottom - 1),
            usedGlobalSearch: false,
            tuning,
            reciprocalScale);
        output.Add(current);
        if (IsStrongAbsoluteCandidate(current, tuning))
            return;

        using var template = new Mat(query.Edges, query.Bounds);
        using var templateFloat = new Mat();
        template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
        using var referencePatch = new Mat(
            referenceDistance,
            new Rect(
                searchDomain.X,
                searchDomain.Y,
                templateFloat.Width + searchDomain.Width - 1,
                templateFloat.Height + searchDomain.Height - 1));
        using var scores = new Mat();
        Cv2.MatchTemplate(
            referencePatch,
            templateFloat,
            scores,
            TemplateMatchModes.CCorr);
        Cv2.Multiply(
            scores,
            1d / Math.Max(1, query.EdgeCount),
            scores);
        CollectCandidates(
            scores,
            query,
            reference,
            referenceDistance,
            request,
            scale,
            new Rect(0, 0, scores.Width, scores.Height),
            usedGlobalSearch: false,
            tuning,
            reciprocalScale,
            output,
            searchDomain.X,
            searchDomain.Y);
    }

    internal static void CollectHistoryCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        foreach (var transform in request.CandidateHistory
            .Where(candidate => candidate?.IsValid is true)
            .TakeLast(StructureRegistrationRules.MaxHistoryCandidates))
        {
            if (Math.Abs((transform.Scale / scale) - 1d)
                > StructureRegistrationRules.ScaleAgreementTolerance)
                continue;
            var referenceX = (int)Math.Round(
                (request.ViewportBounds.X
                    + (query.Bounds.X * scale)
                    - transform.TranslationX) / scale);
            var referenceY = (int)Math.Round(
                (request.ViewportBounds.Y
                    + (query.Bounds.Y * scale)
                    - transform.TranslationY) / scale);
            // 互逆缩放：边界检查必须针对 referenceDistance 所在空间
            var histRefWidth = reciprocalScale.StructureMask?.Width
                ?? reference.Edges.Width;
            var histRefHeight = reciprocalScale.StructureMask?.Height
                ?? reference.Edges.Height;
            if (referenceX < 0
                || referenceY < 0
                || referenceX + query.Bounds.Width
                    >= histRefWidth
                || referenceY + query.Bounds.Height
                    >= histRefHeight)
            {
                continue;
            }
            output.Add(MapStructureEvaluator.Evaluate(
                query,
                reference,
                referenceDistance,
                request,
                scale,
                referenceX,
                referenceY,
                usedGlobalSearch: false,
                tuning,
                reciprocalScale));
        }
    }

    internal static bool IsStrongAbsoluteCandidate(
        MapStructureCandidate candidate,
        MapStructureRegistrationTuning tuning) =>
            candidate.ChamferPixels
                <= tuning.MaximumChamferPixels
                    * StructureRegistrationRules.StrictChamferFactor
        && candidate.EdgeCoverage
            >= tuning.MinimumEdgeCoverage
                + StructureRegistrationRules.StrictEdgeCoverageMargin
        && candidate.OccupancyCoverage
            >= tuning.MinimumOccupancyCoverage
                + StructureRegistrationRules.StrictOccupancyMargin
        && candidate.ConsistentPartitions >= Math.Max(
            StructureRegistrationRules.IsStrongCandidateMinPartitions,
            tuning.MinimumConsistentPartitions);

    internal static void CollectCandidates(
        Mat scores,
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        Rect searchRect,
        bool usedGlobalSearch,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output,
        int originX = 0,
        int originY = 0)
    {
        if (searchRect.Width <= 0 || searchRect.Height <= 0)
            return;
        using var search = new Mat(scores, searchRect).Clone();
        var suppressionRadius = Math.Max(
            tuning.MinimumSpanPixels,
            Math.Min(query.Bounds.Width, query.Bounds.Height) / StructureRegistrationRules.CollectCandidatesSuppressionDivisor);
        for (var index = 0; index < tuning.TopCandidateCount; index++)
        {
            Cv2.MinMaxLoc(search, out var score, out _, out var location, out _);
            if (!double.IsFinite(score))
                break;
            var referenceX = originX + searchRect.X + location.X;
            var referenceY = originY + searchRect.Y + location.Y;
            // Only skip the same integer location here. Nearby points can
            // still be materially better and are deduplicated after their
            // full structural score is known.
            var duplicateRadius = StructureRegistrationRules.CandidateDuplicateRadius;
            if (!output.Any(candidate =>
                    Math.Abs(candidate.Scale - scale) < StructureRegistrationRules.ScaleDuplicateTolerance
                    && Math.Sqrt(
                        Math.Pow(candidate.ReferenceX - referenceX, 2d)
                        + Math.Pow(candidate.ReferenceY - referenceY, 2d))
                        < duplicateRadius))
            {
                output.Add(MapStructureEvaluator.Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    scale,
                    referenceX,
                    referenceY,
                    usedGlobalSearch,
                    tuning,
                    reciprocalScale));
            }
            var left = Math.Max(0, location.X - suppressionRadius);
            var top = Math.Max(0, location.Y - suppressionRadius);
            var right = Math.Min(search.Width, location.X + suppressionRadius + 1);
            var bottom = Math.Min(search.Height, location.Y + suppressionRadius + 1);
            Cv2.Rectangle(
                search,
                new Rect(left, top, right - left, bottom - top),
                Scalar.All(double.PositiveInfinity),
                -1);
        }
    }

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
}
