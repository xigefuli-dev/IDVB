using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Low-cost one-dimensional structure scorer. It intentionally works on
/// projection vectors instead of creating a padded two-dimensional canvas.
/// </summary>
internal static class MapStructureProjectionScorer
{
    internal static double Score(
        Mat queryEdges,
        Mat referenceEdges,
        int referenceX,
        int referenceY,
        int topK = 2)
    {
        _ = topK;
        if (queryEdges.Empty()
            || referenceX < 0
            || referenceY < 0
            || referenceX + queryEdges.Width > referenceEdges.Width
            || referenceY + queryEdges.Height > referenceEdges.Height)
            return 0d;

        // Score both axes from the same two-dimensional patch. Computing the
        // X projection over the full reference height and the Y projection
        // over its full width lets unrelated rooms manufacture a high score.
        using var referencePatch = new Mat(
            referenceEdges,
            new Rect(
                referenceX,
                referenceY,
                queryEdges.Width,
                queryEdges.Height));
        return (Normalize(Correlation(
                    Projection(queryEdges, horizontal: true),
                    Projection(referencePatch, horizontal: true),
                    0))
                + Normalize(Correlation(
                    Projection(queryEdges, horizontal: false),
                    Projection(referencePatch, horizontal: false),
                    0)))
            / 2d;
    }

    private static double[] Projection(Mat binary, bool horizontal)
    {
        var length = horizontal ? binary.Width : binary.Height;
        var values = new double[length];
        var height = binary.Height;
        var width = binary.Width;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (binary.At<byte>(y, x) == 0)
                continue;
            values[horizontal ? x : y]++;
        }
        return values;
    }

    private static double Correlation(double[] query, double[] reference, int start)
    {
        var queryMean = query.Average();
        var referenceMean = 0d;
        for (var i = 0; i < query.Length; i++)
            referenceMean += reference[start + i];
        referenceMean /= query.Length;
        var numerator = 0d;
        var queryVariance = 0d;
        var referenceVariance = 0d;
        for (var i = 0; i < query.Length; i++)
        {
            var q = query[i] - queryMean;
            var r = reference[start + i] - referenceMean;
            numerator += q * r;
            queryVariance += q * q;
            referenceVariance += r * r;
        }
        if (queryVariance < 1e-9d || referenceVariance < 1e-9d)
        {
            var queryMass = query.Sum();
            var referenceMass = 0d;
            for (var i = 0; i < query.Length; i++)
                referenceMass += reference[start + i];
            return 1d - Math.Abs(queryMass - referenceMass)
                / Math.Max(1d, Math.Max(queryMass, referenceMass));
        }
        return numerator / Math.Sqrt(queryVariance * referenceVariance);
    }

    private static double Normalize(double value) =>
        Math.Clamp((value + 1d) / 2d, 0d, 1d);
}
