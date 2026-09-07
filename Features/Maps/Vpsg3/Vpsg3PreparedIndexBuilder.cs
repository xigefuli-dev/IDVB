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

        // 3. Morphological dilation for structural matching tolerance (K5 for 5x5 / +/-2px and K3 for 3x3 / +/-1px)
        using var dilatedK5 = new Mat();
        using var dilatedK3 = new Mat();
        using var kernel5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var kernel3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(edgeImage, dilatedK5, kernel5);
        Cv2.Dilate(edgeImage, dilatedK3, kernel3);

        // 4. Pack into 64-bit row-major bitsets
        var wordsPerRow = (width + 63) / 64;
        var bitsetK5 = new ulong[height * wordsPerRow];
        var bitsetK3 = new ulong[height * wordsPerRow];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * wordsPerRow;
            for (var x = 0; x < width; x++)
            {
                if (dilatedK5.At<byte>(y, x) > 128)
                {
                    bitsetK5[rowOffset + (x >> 6)] |= 1UL << (x & 63);
                }
                if (dilatedK3.At<byte>(y, x) > 128)
                {
                    bitsetK3[rowOffset + (x >> 6)] |= 1UL << (x & 63);
                }
            }
        }

        // 5. Calculate memory footprint
        var objectOverhead = 160L;
        var bitsetBytes = ((bitsetK5.Length + bitsetK3.Length) * 8L) + 48L;
        var totalBytes = objectOverhead + bitsetBytes;

        return new Vpsg3PreparedFloor(
            cacheKey,
            width,
            height,
            edgePixelCount,
            scalePrior,
            wordsPerRow,
            bitsetK5,
            bitsetK3,
            totalBytes);
    }

    /// <summary>
    /// Builds a prepared VPSG 3.0 floor index asynchronously with execution timing.
    /// Synchronously clones the input Mat before background dispatch to guarantee caller lifecycle isolation.
    /// Outer lifecycle ensures ownedMat is always disposed even if cancellation occurs before task dispatch.
    /// </summary>
    public static async Task<Vpsg3IndexBuildResult> BuildFromMatAsync(
        Mat edgeImage,
        Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edgeImage);
        if (edgeImage.Empty())
            throw new ArgumentException("Edge image cannot be empty.", nameof(edgeImage));

        if (cancellationToken.IsCancellationRequested)
        {
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: "Operation was canceled.",
                BuildMilliseconds: 0);
        }

        var ownedMat = edgeImage.Clone();
        var sw = Stopwatch.StartNew();
        var taskExecuted = false;

        try
        {
            return await Task.Run(() =>
            {
                taskExecuted = true;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var floor = BuildFromMat(ownedMat, cacheKey, tuning);
                    sw.Stop();
                    return new Vpsg3IndexBuildResult(
                        Success: true,
                        Floor: floor,
                        ErrorMessage: null,
                        BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    ownedMat.Dispose();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: "Operation was canceled.",
                BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: ex.Message,
                BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (!taskExecuted && !ownedMat.IsDisposed)
            {
                ownedMat.Dispose();
            }
        }
    }

    /// <summary>
    /// Decodes a reference image from disk in the background and constructs a prepared floor index.
    /// Manages the full lifecycle of the loaded Mat entirely within the background task.
    /// </summary>
    public static async Task<Vpsg3IndexBuildResult> BuildFromFileAsync(
        string imagePath,
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
                using var image = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
                if (image.Empty())
                {
                    sw.Stop();
                    return new Vpsg3IndexBuildResult(
                        Success: false,
                        Floor: null,
                        ErrorMessage: $"Failed to decode reference image from '{imagePath}'.",
                        BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
                }

                var floor = BuildFromMat(image, cacheKey, tuning);
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
        var (pitchX, ratioX) = FindDominantPitchNormalized(projX);

        if (pitchX <= 5.0 || ratioX < cfg.PeakRatioThreshold)
        {
            return Vpsg3ScalePrior.Ineligible(
                $"ReferencePeakRatioBelowThreshold ({ratioX:F2} < {cfg.PeakRatioThreshold:F2})",
                pitchX,
                ratioX);
        }

        return new Vpsg3ScalePrior(
            SeedScale: 1.0d,
            PeakRatio: ratioX,
            FastPathEligible: true,
            RejectReason: string.Empty,
            ReferencePitch: pitchX,
            ReferencePeakRatio: ratioX);
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
