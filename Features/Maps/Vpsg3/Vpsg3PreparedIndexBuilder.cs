using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Prepares immutable, resident in-memory VPSG 3.0 floor indices from structural edge images.
/// Extracts dominant pitch priors, builds dilated binary bitsets for fast translation correlation,
/// and computes memory footprints.
/// </summary>
public static class Vpsg3PreparedIndexBuilder
{
    private const double Epsilon = 1e-9d;

    /// <summary>
    /// Builds a prepared VPSG 3.0 floor index synchronously from a reference edge image.
    /// </summary>
    public static Vpsg3PreparedFloor BuildFromMat(
        Mat edgeImage,
        Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(edgeImage);
        if (edgeImage.Empty())
            throw new ArgumentException("Edge image cannot be empty.", nameof(edgeImage));
        if (edgeImage.Type() != MatType.CV_8UC1)
            throw new ArgumentException("Edge image must be single-channel 8-bit.", nameof(edgeImage));

        var cfg = tuning ?? Vpsg3TuningConfig.Default;
        var width = edgeImage.Width;
        var height = edgeImage.Height;

        // 1. Edge pixel count
        var edgePixelCount = Cv2.CountNonZero(edgeImage);

        // 2. Compute reference scale prior via normalized autocorrelation
        var scalePrior = ComputeReferenceScalePrior(edgeImage, edgePixelCount, cfg);

        // 3. 3x3 Morphological dilation for structural matching tolerance
        using var dilated = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(edgeImage, dilated, kernel);

        // 4. Pack into 64-bit row-major bitset
        var wordsPerRow = (width + 63) / 64;
        var bitset = new ulong[height * wordsPerRow];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * wordsPerRow;
            for (var x = 0; x < width; x++)
            {
                if (dilated.At<byte>(y, x) > 128)
                {
                    bitset[rowOffset + (x >> 6)] |= 1UL << (x & 63);
                }
            }
        }

        // 5. Calculate memory footprint
        var objectOverhead = 128L;
        var bitsetBytes = (bitset.Length * 8L) + 24L;
        var totalBytes = objectOverhead + bitsetBytes;

        return new Vpsg3PreparedFloor(
            cacheKey,
            width,
            height,
            edgePixelCount,
            scalePrior,
            wordsPerRow,
            bitset,
            totalBytes);
    }

    /// <summary>
    /// Builds a prepared VPSG 3.0 floor index asynchronously with execution timing.
    /// </summary>
    public static async Task<Vpsg3IndexBuildResult> BuildFromMatAsync(
        Mat edgeImage,
        Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var floor = BuildFromMat(edgeImage, cacheKey, tuning);
                sw.Stop();
                return new Vpsg3IndexBuildResult(
                    Success: true,
                    Floor: floor,
                    ErrorMessage: null,
                    BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: ex.Message,
                BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Computes dominant pitch and peak ratio from edge projections.
    /// </summary>
    public static Vpsg3ScalePrior ComputeReferenceScalePrior(
        Mat edgeImage,
        int edgePixelCount,
        Vpsg3TuningConfig cfg)
    {
        if (edgePixelCount < cfg.MinEdgePixels)
        {
            return Vpsg3ScalePrior.Ineligible(
                $"EdgePixelCountBelowThreshold ({edgePixelCount} < {cfg.MinEdgePixels})");
        }

        var projX = Compute1DProjection(edgeImage, axis: 0);
        var projY = Compute1DProjection(edgeImage, axis: 1);

        var (pitchX, ratioX) = FindDominantPitchNormalized(projX);
        var (pitchY, ratioY) = FindDominantPitchNormalized(projY);

        // Select the axis with higher peak confidence
        var (bestPitch, bestRatio) = ratioX >= ratioY
            ? (pitchX, ratioX)
            : (pitchY, ratioY);

        if (bestPitch <= 5.0 || bestRatio < cfg.PeakRatioThreshold)
        {
            return Vpsg3ScalePrior.Ineligible(
                $"ReferencePeakRatioBelowThreshold ({bestRatio:F2} < {cfg.PeakRatioThreshold:F2})",
                bestPitch,
                bestRatio);
        }

        return new Vpsg3ScalePrior(
            SeedScale: 1.0d,
            PeakRatio: bestRatio,
            FastPathEligible: true,
            RejectReason: string.Empty,
            ReferencePitch: bestPitch,
            ReferencePeakRatio: bestRatio);
    }

    /// <summary>
    /// Computes 1D projection signal along the chosen axis.
    /// </summary>
    public static double[] Compute1DProjection(Mat edgeMask, int axis)
    {
        var width = edgeMask.Width;
        var height = edgeMask.Height;

        if (axis == 0) // Column projection along X
        {
            var proj = new double[width];
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var y = 0; y < height; y++)
                {
                    if (edgeMask.At<byte>(y, x) > 128)
                        sum++;
                }
                proj[x] = sum;
            }
            return proj;
        }
        else // Row projection along Y
        {
            var proj = new double[height];
            for (var y = 0; y < height; y++)
            {
                var sum = 0.0;
                for (var x = 0; x < width; x++)
                {
                    if (edgeMask.At<byte>(y, x) > 128)
                        sum++;
                }
                proj[y] = sum;
            }
            return proj;
        }
    }

    /// <summary>
    /// Extracts dominant pitch and peak-to-median ratio via normalized autocorrelation.
    /// </summary>
    public static (double Pitch, double PeakRatio) FindDominantPitchNormalized(double[] signal)
    {
        var n = signal.Length;
        if (n < 40)
            return (0.0, 0.0);

        var sum = 0.0;
        for (var i = 0; i < n; i++)
            sum += signal[i];
        var mean = sum / n;

        var centered = new double[n];
        var variance = 0.0;
        for (var i = 0; i < n; i++)
        {
            centered[i] = signal[i] - mean;
            variance += centered[i] * centered[i];
        }

        if (variance < Epsilon)
            return (0.0, 0.0);

        const int minLag = 12;
        var maxLag = n / 2;
        if (maxLag <= minLag)
            return (0.0, 0.0);

        var bestLag = 0;
        var maxR = -1.0;
        var rValues = new List<double>(maxLag - minLag);

        for (var lag = minLag; lag < maxLag; lag++)
        {
            var dot = 0.0;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                dot += centered[i] * centered[i + lag];
            }

            var r = dot / variance;
            rValues.Add(r);

            if (r > maxR)
            {
                maxR = r;
                bestLag = lag;
            }
        }

        if (rValues.Count == 0 || maxR <= 0.05)
            return (0.0, 0.0);

        var sortedR = rValues.Select(Math.Abs).OrderBy(x => x).ToList();
        var medianR = sortedR[sortedR.Count / 2];
        var peakRatio = maxR / Math.Max(0.01, medianR);

        return (bestLag, peakRatio);
    }
}
