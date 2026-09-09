using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production scale solver for VPSG 3.0 (Method S-B).
/// Estimates high-confidence seed scale prior from 1D structural projections and autocorrelation.
/// Operates directly against resident PreparedFloor.ScalePrior without opening reference images or clamping.
/// </summary>
public static class Vpsg3ScaleSolver
{
    private const double Epsilon = 1e-9d;

    /// <summary>
    /// Solves for the seed scale prior given live observation and prepared floor index.
    /// </summary>
    public static Vpsg3ScaleResult Solve(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3TuningConfig.Default;
        var sc = scratch ?? Vpsg3SolverScratch.Current;

        var refPrior = preparedFloor.ScalePrior;
        if (!refPrior.FastPathEligible || refPrior.ReferencePitch <= Epsilon)
        {
            return Vpsg3ScaleResult.Failed(
                Vpsg3ScaleStatus.DegenerateSignal,
                $"PreparedFloorReferenceIneligible: {refPrior.RejectReason}",
                refPrior.ReferencePeakRatio);
        }

        if (observation.EdgePixelCount < cfg.MinEdgePixels)
        {
            return Vpsg3ScaleResult.Failed(
                Vpsg3ScaleStatus.InsufficientEdgePixels,
                $"ObservationEdgePixelsBelowThreshold: {observation.EdgePixelCount} < {cfg.MinEdgePixels}");
        }

        var edges = observation.ObservedEdges;
        var width = edges.Width;
        var height = edges.Height;
        sc.EnsureScaleCapacity(width, height);

        // 1. Compute 1D projection along X axis (zero managed heap allocation)
        var buffer = sc.EdgeMaskBuffer;
        if (edges.IsContinuous())
        {
            Marshal.Copy(edges.Data, buffer, 0, width * height);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(edges.Ptr(y), buffer, y * width, width);
            }
        }

        var projX = sc.ProjectionBufferX.AsSpan(0, width);
        projX.Clear();

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                if (buffer[rowOffset + x] > 128)
                {
                    projX[x]++;
                }
            }
        }

        // 2. Find dominant query pitch on X axis
        var (pitchX, ratioX) = FindDominantPitch(projX, sc, refPrior.ReferencePitch * cfg.MinSupportedScale, refPrior.ReferencePitch * cfg.MaxSupportedScale);

        // 3. PeakRatio Gating: effective peak ratio is min(live, reference)
        var peakRatio = Math.Min(ratioX, refPrior.ReferencePeakRatio);
        if (peakRatio < cfg.PeakRatioThreshold || pitchX <= 5.0d)
        {
            return Vpsg3ScaleResult.Failed(
                Vpsg3ScaleStatus.PeakRatioBelowThreshold,
                $"PeakRatioBelowThreshold: {peakRatio:F2} < {cfg.PeakRatioThreshold:F2} (Pitch={pitchX:F1})",
                peakRatio);
        }

        // 4. Compute EstimatedScale = LivePitch / ReferencePitch
        var estimatedScale = pitchX / refPrior.ReferencePitch;

        // 5. Supported Scale Domain Gating (strictly enforce [MinSupportedScale, MaxSupportedScale], NO CLAMP!)
        if (estimatedScale < cfg.MinSupportedScale || estimatedScale > cfg.MaxSupportedScale)
        {
            return Vpsg3ScaleResult.Failed(
                Vpsg3ScaleStatus.ScaleOutOfSupportedRange,
                $"EstimatedScaleOutOfRange: {estimatedScale:F4} not in [{cfg.MinSupportedScale:F2}, {cfg.MaxSupportedScale:F2}]",
                peakRatio);
        }

        return new Vpsg3ScaleResult(
            Vpsg3ScaleStatus.Success,
            estimatedScale,
            peakRatio,
            0,
            string.Empty);
    }

    private static (double Pitch, double PeakRatio) FindDominantPitch(
        ReadOnlySpan<double> signal,
        Vpsg3SolverScratch scratch, double minPitch, double maxPitch)
    {
        var n = signal.Length;
        if (n < 40) return (0.0d, 0.0d);

        var sum = 0.0d;
        for (var i = 0; i < n; i++)
            sum += signal[i];
        var mean = sum / n;

        var centered = scratch.CenteredSignalBuffer.AsSpan(0, n);
        var variance = 0.0d;
        for (var i = 0; i < n; i++)
        {
            var diff = signal[i] - mean;
            centered[i] = diff;
            variance += diff * diff;
        }

        if (variance < Epsilon) return (0.0d, 0.0d);

        const int minLag = 12;
        var maxLag = n / 2;
        if (maxLag <= minLag) return (0.0d, 0.0d);

        var totalLags = maxLag - minLag;
        var autocorr = scratch.AutocorrBuffer.AsSpan(0, totalLags);
        var rawAutocorr = scratch.ProjectionBufferY.AsSpan(0, totalLags);
        var bestLag = 0;
        var maxR = -1.0d;

        for (var lag = minLag; lag < maxLag; lag++)
        {
            var dot = 0.0d;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                dot += centered[i] * centered[i + lag];
            }

            var r = dot / variance;
            var idx = lag - minLag;
            rawAutocorr[idx] = r;
            autocorr[idx] = Math.Abs(r);

            if (r > maxR && lag >= minPitch && lag <= maxPitch)
            {
                maxR = r;
                bestLag = lag;
            }
        }

        if (maxR <= 0.05d || bestLag <= 0) return (0.0d, 0.0d);

        // Harmonic ambiguity resolution: prioritize fundamental pitch over harmonic multiples
        // If a smaller local peak exists in [minPitch, maxPitch] with r >= 0.80 * maxR and bestLag is roughly an integer multiple,
        // select the smaller fundamental pitch to prevent locking into higher harmonics (e.g. 0.52x/1.57x on periodic maps).
        var searchStart = (int)Math.Max(minLag + 1, Math.Ceiling(minPitch));
        var searchEnd = (int)Math.Min(maxLag - 1, Math.Floor(maxPitch));
        for (var lag = searchStart; lag < bestLag - 1 && lag < searchEnd; lag++)
        {
            var idx = lag - minLag;
            if (idx <= 0 || idx >= totalLags - 1) continue;
            var r = rawAutocorr[idx];
            if (r > rawAutocorr[idx - 1] && r > rawAutocorr[idx + 1] && r >= 0.80d * maxR)
            {
                var ratio = (double)bestLag / lag;
                var k = Math.Round(ratio);
                if (k >= 2 && Math.Abs(ratio - k) < 0.15d)
                {
                    bestLag = lag;
                    maxR = r;
                    break;
                }
            }
        }

        // In-place sort of absolute autocorrelation values in scratch to find median without heap allocation
        autocorr.Sort();
        var medianR = autocorr[totalLags / 2];
        var peakRatio = maxR / Math.Max(0.01d, medianR);

        // Sub-pixel parabolic interpolation on continuous autocorrelation signal
        var refinedPitch = (double)bestLag;
        var bIdx = bestLag - minLag;
        if (bIdx > 0 && bIdx < totalLags - 1)
        {
            var y0 = rawAutocorr[bIdx - 1];
            var y1 = rawAutocorr[bIdx];
            var y2 = rawAutocorr[bIdx + 1];
            if (y1 > y0 && y1 > y2)
            {
                var denom = 2.0d * (2.0d * y1 - y0 - y2);
                if (denom > 1e-9d)
                {
                    var delta = (y2 - y0) / denom;
                    if (Math.Abs(delta) <= 0.5d)
                    {
                        refinedPitch = bestLag + delta;
                    }
                }
            }
        }

        return (refinedPitch, peakRatio);
    }
}
