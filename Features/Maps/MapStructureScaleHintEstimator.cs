using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record MapStructureScaleHint(
    double Scale,
    double Confidence,
    int ReferencePairCount,
    int LivePairCount);

/// <summary>
/// Estimates a scale range from edge spacing only. It is a search hint, never
/// an alignment decision.
/// </summary>
internal static class MapStructureScaleHintEstimator
{
    private const int MaximumSamplePoints = 192;
    private const int MinimumPairCount = 80;
    private const int HistogramBinCount = 48;
    private const int HintScaleCount = 65;

    internal static bool TryEstimate(
        MapStructureFeatures reference,
        MapStructureFeatures live,
        double minimumScale,
        double maximumScale,
        out MapStructureScaleHint hint)
    {
        hint = new(0d, 0d, 0, 0);
        if (reference.Edges.Empty() || live.Edges.Empty())
            return false;

        var referenceLogs = BuildPairDistanceLogs(reference.Edges);
        var liveLogs = BuildPairDistanceLogs(live.Edges);
        if (referenceLogs.Length < MinimumPairCount
            || liveLogs.Length < MinimumPairCount)
        {
            return false;
        }

        minimumScale = Math.Max(0.05d, minimumScale);
        maximumScale = Math.Max(minimumScale, maximumScale);
        var minimumLog = Math.Min(
            referenceLogs.Min(),
            liveLogs.Min() - Math.Log(maximumScale));
        var maximumLog = Math.Max(
            referenceLogs.Max(),
            liveLogs.Max() - Math.Log(minimumScale));
        var binWidth = (maximumLog - minimumLog) / HistogramBinCount;
        if (!double.IsFinite(binWidth) || binWidth <= 1e-6d)
            return false;

        var referenceHistogram = BuildHistogram(
            referenceLogs,
            minimumLog,
            binWidth);
        var scales = Enumerable.Range(0, HintScaleCount)
            .Select(index => minimumScale * Math.Exp(
                Math.Log(maximumScale / minimumScale)
                    * index / (HintScaleCount - 1d)))
            .ToArray();
        var scores = scales
            .Select(scale => Correlate(
                referenceHistogram,
                BuildHistogram(
                    liveLogs.Select(value => value - Math.Log(scale)).ToArray(),
                    minimumLog,
                    binWidth)))
            .ToArray();
        var bestIndex = Enumerable.Range(0, scores.Length)
            .MaxBy(index => scores[index]);
        var bestScore = scores[bestIndex];
        var secondScore = scores
            .Where((_, index) => Math.Abs(index - bestIndex) > 2)
            .DefaultIfEmpty(0d)
            .Max();
        var margin = Math.Max(0d, bestScore - secondScore);
        var confidence = Math.Clamp(
            0.20d + (bestScore * 0.35d) + (margin * 3d),
            0d,
            0.98d);

        var logScale = Math.Log(scales[bestIndex]);
        if (bestIndex > 0 && bestIndex + 1 < scores.Length)
        {
            var curvature = scores[bestIndex - 1]
                - (2d * scores[bestIndex])
                + scores[bestIndex + 1];
            if (curvature < -1e-9d)
            {
                var offset = (scores[bestIndex - 1] - scores[bestIndex + 1])
                    / (2d * curvature);
                logScale += Math.Clamp(
                    offset,
                    -1d,
                    1d) * Math.Log(maximumScale / minimumScale)
                    / (HintScaleCount - 1d);
            }
        }

        var estimatedScale = Math.Clamp(
            Math.Exp(logScale),
            minimumScale,
            maximumScale);
        hint = new(
            estimatedScale,
            confidence,
            referenceLogs.Length,
            liveLogs.Length);
        return double.IsFinite(hint.Scale);
    }

    private static double[] BuildPairDistanceLogs(Mat edges)
    {
        var points = MapStructureScaleSearch.FindNonZeroPoints(edges);
        if (points.Length > MaximumSamplePoints)
        {
            var sampled = new Point[MaximumSamplePoints];
            var stride = (points.Length - 1d) / (MaximumSamplePoints - 1d);
            for (var index = 0; index < sampled.Length; index++)
                sampled[index] = points[(int)Math.Round(index * stride)];
            points = sampled;
        }

        // ponytail: bounded O(n²) pairs keep this optional hint cheap; replace
        // with a spatial sampler only if real captures show it is a bottleneck.
        var distances = new List<double>(points.Length * 8);
        for (var first = 0; first < points.Length; first++)
        {
            for (var second = first + 1; second < points.Length; second++)
            {
                var dx = points[first].X - points[second].X;
                var dy = points[first].Y - points[second].Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (double.IsFinite(distance) && distance > 1.5d)
                    distances.Add(Math.Log(distance));
            }
        }
        return distances.ToArray();
    }

    private static double[] BuildHistogram(
        IReadOnlyList<double> values,
        double minimumLog,
        double binWidth)
    {
        var histogram = new double[HistogramBinCount];
        foreach (var value in values)
        {
            var index = (int)Math.Floor((value - minimumLog) / binWidth);
            if (index == HistogramBinCount)
                index--;
            if ((uint)index < HistogramBinCount)
                histogram[index]++;
        }

        var smoothed = new double[HistogramBinCount];
        for (var index = 0; index < histogram.Length; index++)
        {
            var previous = index > 0 ? histogram[index - 1] : 0d;
            var next = index + 1 < histogram.Length
                ? histogram[index + 1]
                : 0d;
            smoothed[index] = (previous + (2d * histogram[index]) + next) / 4d;
        }
        return smoothed;
    }

    private static double Correlate(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second)
    {
        var firstNorm = Math.Sqrt(first.Sum(value => value * value));
        var secondNorm = Math.Sqrt(second.Sum(value => value * value));
        if (firstNorm <= 1e-9d || secondNorm <= 1e-9d)
            return 0d;
        var dot = 0d;
        for (var index = 0; index < first.Count; index++)
            dot += first[index] * second[index];
        return Math.Clamp(dot / (firstNorm * secondNorm), 0d, 1d);
    }
}
