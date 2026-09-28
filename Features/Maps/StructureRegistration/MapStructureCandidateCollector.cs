using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapStructureCandidateCollector
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
        List<MapStructureCandidate> output,
        bool allowTemplateSearch = true)
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
        if (ScanExecutionContext.Current is { } scan && scan.HasAlignmentConstraint(request))
        {
            // The gate-anchored seed already passed dense identity evidence. If
            // its rounded formal pose passes every absolute/identity gate too,
            // evaluating all 48 neighbours adds no acceptance evidence. Keep the
            // bounded repair search only for a pose that actually needs repair.
            if (MapStructureValidator.ValidateAbsolute(current, tuning,
                restrictedSearch: true, request) == MapStructureRejectionReason.None)
                return;
            // Coarse registration rounds translation into reference pixels. Keep
            // the bounded neighbouring poses available to original-frame validation
            // instead of accepting its single best average-distance location.
            for (var dx = -3; dx <= 3; dx++)
            for (var dy = -3; dy <= 3; dy++)
            {
                if (!scan.CanCompute) return;
                if (dx == 0 && dy == 0) continue;
                var x = expected.X + dx;
                var y = expected.Y + dy;
                if (!searchDomain.Contains(new Point(x, y))) continue;
                output.Add(MapStructureEvaluator.Evaluate(query, reference, referenceDistance,
                    request, scale, x, y, false, tuning, reciprocalScale));
            }
            // The caller ranks these poses using both formal and identity gates.
            return;
        }
        if (IsStrongAbsoluteCandidate(current, tuning))
            return;
        if (!allowTemplateSearch)
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
        var historyLimit = ResolveHistoryCandidateLimit(tuning);
        foreach (var transform in request.CandidateHistory
            .Where(candidate => candidate?.IsValid is true)
            .TakeLast(historyLimit))
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

    internal static int ResolveHistoryCandidateLimit(
        MapStructureRegistrationTuning tuning) =>
        tuning.Channel == MapAlignmentChannel.LowStructure
            ? 0
            : StructureRegistrationRules.MaxHistoryCandidates;

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
        var candidateLimit = tuning.Channel == MapAlignmentChannel.LowStructure
            ? Math.Min(tuning.TopCandidateCount, tuning.LowStructureTranslationTopK)
            : tuning.TopCandidateCount;
        for (var index = 0; index < candidateLimit; index++)
        {
            Cv2.MinMaxLoc(search, out var score, out _, out var location, out _);
            if (!double.IsFinite(score))
                break;
            var referenceX = originX + searchRect.X + location.X;
            var referenceY = originY + searchRect.Y + location.Y;
            // Only skip the same integer location here. Nearby points can
            // still be materially better and are deduplicated after their
            // full structural score is known.
            var isLowStructure =
                tuning.Channel == MapAlignmentChannel.LowStructure;
            var duplicateRadius = isLowStructure
                ? tuning.CandidateDuplicateRadius
                : StructureRegistrationRules.CandidateDuplicateRadius;
            if (!output.Any(candidate =>
                    Math.Abs(candidate.Scale - scale) <
                        (isLowStructure
                            ? tuning.ScaleDuplicateTolerance
                            : StructureRegistrationRules.ScaleDuplicateTolerance)
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

    internal static void CollectLowStructureCoarseCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> output)
    {
        var matchingEdges = reciprocalScale.Edges ?? reference.Edges;
        if (query.Bounds.Width <= 0
            || query.Bounds.Height <= 0
            || query.Bounds.Width >= matchingEdges.Width
            || query.Bounds.Height >= matchingEdges.Height)
            return;
        using var queryEdges = new Mat(query.Edges, query.Bounds);
        var downsample = Math.Max(1, tuning.FastCoarseDownsampleFactor);
        var templateWidth = Math.Max(1, queryEdges.Width / downsample);
        var templateHeight = Math.Max(1, queryEdges.Height / downsample);
        var referenceWidth = Math.Max(1, referenceDistance.Width / downsample);
        var referenceHeight = Math.Max(1, referenceDistance.Height / downsample);
        if (templateWidth < tuning.FastCoarseMinimumTemplateDimension
            || templateHeight < tuning.FastCoarseMinimumTemplateDimension
            || templateWidth >= referenceWidth
            || templateHeight >= referenceHeight)
            return;

        using var coarseTemplate = new Mat();
        Cv2.Resize(
            queryEdges,
            coarseTemplate,
            new Size(templateWidth, templateHeight),
            interpolation: InterpolationFlags.Area);
        using var coarseTemplateFloat = new Mat();
        coarseTemplate.ConvertTo(
            coarseTemplateFloat,
            MatType.CV_32FC1,
            1d / 255d);
        using var coarseReferenceDistance = new Mat();
        Cv2.Resize(
            referenceDistance,
            coarseReferenceDistance,
            new Size(referenceWidth, referenceHeight),
            interpolation: InterpolationFlags.Area);
        using var scores = new Mat();
        Cv2.MatchTemplate(
            coarseReferenceDistance,
            coarseTemplateFloat,
            scores,
            TemplateMatchModes.CCorr);
        Cv2.Multiply(
            scores,
            1d / Math.Max(1, Cv2.CountNonZero(coarseTemplate)),
            scores);

        // Sparse B1F corridors repeat throughout the reference. Distance-map
        // peaks can therefore miss the actual room even when its scale is
        // correct. Add the strongest appearance location as one more coarse
        // hypothesis; it still has to pass the original-pixel structure gate.
        if (query.Appearance is not null
            && !query.Appearance.Empty()
            && !reference.NormalizedGray.Empty())
        {
            using var grayTemplate = new Mat(query.Appearance, query.Bounds);
            if (grayTemplate.Width < reference.NormalizedGray.Width
                && grayTemplate.Height < reference.NormalizedGray.Height)
            {
                using var grayScores = new Mat();
                Cv2.MatchTemplate(
                    reference.NormalizedGray,
                    grayTemplate,
                    grayScores,
                    TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(
                    grayScores,
                    out _,
                    out var maximum,
                    out _,
                    out var appearanceLocation);
                if (double.IsFinite(maximum))
                {
                    output.Add(MapStructureEvaluator.Evaluate(
                        query,
                        reference,
                        referenceDistance,
                        request,
                        scale,
                        appearanceLocation.X,
                        appearanceLocation.Y,
                        usedGlobalSearch: true,
                        tuning,
                        reciprocalScale) with
                    {
                        FromAppearanceSearch = true,
                        AppearanceCorrelation = maximum
                    });
                }
            }
        }

        var coordinateScaleX = (double)referenceDistance.Width / referenceWidth;
        var coordinateScaleY = (double)referenceDistance.Height / referenceHeight;
        var suppression = Math.Max(2, tuning.FastCoarseNmsRadius);
        var maximumCandidatesPerScale = Math.Min(
            tuning.LowStructureTranslationTopK,
            2);
        for (var index = 0;
             index < maximumCandidatesPerScale
                && output.Count(candidate =>
                    Math.Abs(candidate.Scale - scale)
                        < tuning.ScaleDuplicateTolerance)
                    < maximumCandidatesPerScale;
             index++)
        {
            Cv2.MinMaxLoc(scores, out var minimum, out _, out var location, out _);
            if (!double.IsFinite(minimum))
                break;
            var x = Math.Clamp(
                (int)Math.Round(location.X * coordinateScaleX),
                0,
                referenceDistance.Width - query.Bounds.Width);
            var y = Math.Clamp(
                (int)Math.Round(location.Y * coordinateScaleY),
                0,
                referenceDistance.Height - query.Bounds.Height);
            if (output.Any(candidate =>
                    Math.Abs(candidate.Scale - scale) < tuning.ScaleDuplicateTolerance
                    && Math.Abs(candidate.ReferenceX - x) < tuning.CandidateDuplicateRadius
                    && Math.Abs(candidate.ReferenceY - y) < tuning.CandidateDuplicateRadius))
            {
                Suppress(location);
                continue;
            }
            output.Add(MapStructureEvaluator.Evaluate(
                query,
                reference,
                referenceDistance,
                request,
                scale,
                x,
                y,
                usedGlobalSearch: true,
                tuning,
                reciprocalScale));
            Suppress(location);

            void Suppress(Point peak)
            {
                var left = Math.Max(0, peak.X - suppression);
                var top = Math.Max(0, peak.Y - suppression);
                var right = Math.Min(scores.Width, peak.X + suppression + 1);
                var bottom = Math.Min(scores.Height, peak.Y + suppression + 1);
                Cv2.Rectangle(
                    scores,
                    new Rect(left, top, right - left, bottom - top),
                    Scalar.All(double.PositiveInfinity),
                    -1);
            }
        }
    }

}
