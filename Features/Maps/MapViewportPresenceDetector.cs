using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record MapViewportColorSignature(
    IReadOnlyList<double> Histogram,
    double BlueGrayFraction);

public sealed record MapViewportPresenceResult(
    bool IsPresent,
    string Mode,
    double Score,
    double BlueGrayFraction);

/// <summary>
/// Lightweight guard that distinguishes the native blue-gray map viewport
/// from a stable gameplay frame before the expensive alignment pipeline runs.
/// </summary>
public static class MapViewportPresenceDetector
{
    public const double MinimumReferenceSimilarity = 0.70d;
    public const double MinimumBlueGrayFraction = 0.60d;

    private const int HueBins = 18;
    private const int SaturationBins = 6;
    private const int SignatureWidth = 160;
    private const int SignatureHeight = 100;

    public static MapViewportColorSignature CreateSignature(Mat viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (viewport.Empty())
            return new MapViewportColorSignature([], 0d);

        using var bgr = new Mat();
        switch (viewport.Channels())
        {
            case 4:
                Cv2.CvtColor(viewport, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                viewport.CopyTo(bgr);
                break;
            default:
                Cv2.CvtColor(viewport, bgr, ColorConversionCodes.GRAY2BGR);
                break;
        }

        using var resized = new Mat();
        Cv2.Resize(
            bgr,
            resized,
            new Size(SignatureWidth, SignatureHeight),
            interpolation: InterpolationFlags.Area);
        using var hsv = new Mat();
        Cv2.CvtColor(resized, hsv, ColorConversionCodes.BGR2HSV);

        var histogram = new double[HueBins * SaturationBins];
        var blueGrayPixels = 0;
        var rows = hsv.Rows;
        var columns = hsv.Cols;
        var pixelCount = rows * columns;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var pixel = hsv.At<Vec3b>(y, x);
                var hueBin = Math.Min(HueBins - 1, pixel.Item0 * HueBins / 180);
                var saturationBin = Math.Min(
                    SaturationBins - 1,
                    pixel.Item1 * SaturationBins / 256);
                var index = (hueBin * SaturationBins) + saturationBin;
                histogram[index]++;

                if (pixel.Item0 is >= 90 and <= 130 && pixel.Item1 < 140)
                    blueGrayPixels++;
            }
        }

        if (pixelCount > 0)
        {
            for (var i = 0; i < histogram.Length; i++)
                histogram[i] /= pixelCount;
        }

        return new MapViewportColorSignature(
            histogram,
            pixelCount > 0 ? blueGrayPixels / (double)pixelCount : 0d);
    }

    public static MapViewportPresenceResult Evaluate(
        Mat viewport,
        MapViewportColorSignature? reference = null)
    {
        var candidate = CreateSignature(viewport);
        if (reference is not null
            && reference.Histogram.Count == candidate.Histogram.Count
            && reference.Histogram.Count > 0)
        {
            var similarity = CosineSimilarity(
                reference.Histogram,
                candidate.Histogram);
            return new MapViewportPresenceResult(
                similarity >= MinimumReferenceSimilarity,
                "reference-hsv",
                similarity,
                candidate.BlueGrayFraction);
        }

        return new MapViewportPresenceResult(
            candidate.BlueGrayFraction >= MinimumBlueGrayFraction,
            "blue-gray-fallback",
            candidate.BlueGrayFraction,
            candidate.BlueGrayFraction);
    }

    private static double CosineSimilarity(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second)
    {
        var dot = 0d;
        var firstLength = 0d;
        var secondLength = 0d;
        for (var i = 0; i < first.Count; i++)
        {
            dot += first[i] * second[i];
            firstLength += first[i] * first[i];
            secondLength += second[i] * second[i];
        }

        var denominator = Math.Sqrt(firstLength * secondLength);
        return denominator > 1e-12 ? dot / denominator : 0d;
    }
}
