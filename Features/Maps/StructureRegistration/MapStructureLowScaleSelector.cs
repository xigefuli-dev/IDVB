using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Ranks scale hypotheses from the visible local structure itself. Unlike the
/// former whole-bounds ratio, the score is translation invariant and does not
/// assume that the explored fragment spans the complete reference floor.
/// </summary>
internal static class MapStructureLowScaleSelector
{
    internal static IReadOnlyList<double> Rank(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        MapStructureRegistrationTuning tuning)
    {
        var scales = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
            tuning.LowStructureMinimumScale,
            tuning.LowStructureMaximumScale,
            tuning.LowStructureScaleHypothesisCount,
            tuning.MinimumUsableScale);
        var points = MapStructureScaleSearch.FindNonZeroPoints(live.Edges);
        if (points.Length < tuning.MinimumEdgePixels)
            return [];
        var bounds = Cv2.BoundingRect(points);
        if (bounds.Width < tuning.MinimumSpanPixels
            || bounds.Height < tuning.MinimumSpanPixels)
            return [];

        using var livePatch = new Mat(live.Edges, bounds);
        var factor = Math.Max(4, tuning.FastCoarseDownsampleFactor * 2);
        var referenceDistance = reference.GetOrCreateClippedReferenceDistanceMap(
            tuning.DistanceClipPixels);
        var coarseReferenceSize = new Size(
            Math.Max(1, referenceDistance.Width / factor),
            Math.Max(1, referenceDistance.Height / factor));
        using var coarseReference = new Mat();
        Cv2.Resize(
            referenceDistance,
            coarseReference,
            coarseReferenceSize,
            interpolation: InterpolationFlags.Area);

        var ranked = ScoreScales(scales, factor, coarseReference);
        var ordered = ranked
            .OrderBy(item => item.Cost)
            .ThenBy(item => Math.Abs(Math.Log(item.Scale)))
            .ToArray();
        if (ordered.Length == 0)
            return [];

        // The thirteen-point cold grid is only a basin locator. Its adjacent
        // points are roughly 11-12% apart, which is far too coarse for an
        // overlay transform and caused a persistent B1F ghost image even on
        // accepted matches. Refine the winning basin in this already
        // downsampled selector, then spend exact registration budget on only
        // the best fine scale and its two immediate neighbours.
        var coarseGrid = scales.OrderBy(scale => scale).ToArray();
        var coarseWinnerIndex = Array.FindIndex(
            coarseGrid,
            scale => Math.Abs(scale - ordered[0].Scale) < 1e-9d);
        var fineGrid = BuildFineScaleGrid(
            coarseGrid,
            coarseWinnerIndex,
            Math.Min(tuning.ScaleSearchStep, 0.005d));
        var fineFactor = Math.Max(2, tuning.FastCoarseDownsampleFactor);
        using var fineReference = new Mat();
        Cv2.Resize(
            referenceDistance,
            fineReference,
            new Size(
                Math.Max(1, referenceDistance.Width / fineFactor),
                Math.Max(1, referenceDistance.Height / fineFactor)),
            interpolation: InterpolationFlags.Area);
        var fineOrdered = ScoreScales(fineGrid, fineFactor, fineReference)
            .OrderBy(item => item.Cost)
            .ThenBy(item => Math.Abs(Math.Log(item.Scale)))
            .ToArray();
        if (fineOrdered.Length == 0)
            return [];

        var sortedFine = fineGrid.OrderBy(scale => scale).ToArray();
        var fineWinner = fineOrdered[0].Scale;
        var fineWinnerIndex = Array.FindIndex(
            sortedFine,
            scale => Math.Abs(scale - fineWinner) < 1e-9d);
        var bracket = new List<double> { fineWinner };
        if (fineWinnerIndex > 0)
            bracket.Add(sortedFine[fineWinnerIndex - 1]);
        if (fineWinnerIndex >= 0 && fineWinnerIndex + 1 < sortedFine.Length)
            bracket.Add(sortedFine[fineWinnerIndex + 1]);
        return bracket
            .Concat(fineOrdered.Select(item => item.Scale))
            .DistinctBy(scale => Math.Round(scale, 9))
            .ToArray();

        List<(double Scale, double Cost)> ScoreScales(
            IEnumerable<double> candidates,
            int scoringFactor,
            Mat scoringReference)
        {
            var scoresByScale = new List<(double Scale, double Cost)>();
            foreach (var scale in candidates)
            {
                var templateSize = new Size(
                    Math.Max(1, (int)Math.Round(
                        livePatch.Width / scale / scoringFactor)),
                    Math.Max(1, (int)Math.Round(
                        livePatch.Height / scale / scoringFactor)));
                if (templateSize.Width < 3
                    || templateSize.Height < 3
                    || templateSize.Width >= scoringReference.Width
                    || templateSize.Height >= scoringReference.Height)
                {
                    continue;
                }
                using var template = new Mat();
                Cv2.Resize(
                    livePatch,
                    template,
                    templateSize,
                    interpolation: InterpolationFlags.Area);
                using var templateFloat = new Mat();
                template.ConvertTo(
                    templateFloat,
                    MatType.CV_32FC1,
                    1d / 255d);
                var mass = Cv2.Sum(templateFloat).Val0;
                if (mass < 1d)
                    continue;
                using var scoreMap = new Mat();
                Cv2.MatchTemplate(
                    scoringReference,
                    templateFloat,
                    scoreMap,
                    TemplateMatchModes.CCorr);
                Cv2.MinMaxLoc(
                    scoreMap,
                    out var minimum,
                    out _,
                    out _,
                    out _);
                if (double.IsFinite(minimum))
                    scoresByScale.Add((scale, minimum / mass));
            }
            return scoresByScale;
        }
    }

    internal static IReadOnlyList<double> BuildFineScaleGrid(
        IReadOnlyList<double> coarseGrid,
        int winnerIndex,
        double maximumRelativeStep)
    {
        if (coarseGrid.Count == 0 || winnerIndex < 0)
            return [];
        winnerIndex = Math.Min(winnerIndex, coarseGrid.Count - 1);
        var lower = coarseGrid[Math.Max(0, winnerIndex - 1)];
        var upper = coarseGrid[Math.Min(coarseGrid.Count - 1, winnerIndex + 1)];
        if (!double.IsFinite(lower)
            || !double.IsFinite(upper)
            || lower <= 0d
            || upper < lower)
        {
            return [];
        }
        if (Math.Abs(upper - lower) < 1e-9d)
            return [lower];

        var logSpan = Math.Log(upper / lower);
        var logStep = Math.Clamp(maximumRelativeStep, 0.0025d, 0.02d);
        var segments = Math.Clamp(
            (int)Math.Ceiling(logSpan / logStep),
            2,
            48);
        return Enumerable.Range(0, segments + 1)
            .Select(index => lower * Math.Exp(logSpan * index / segments))
            .ToArray();
    }
}
