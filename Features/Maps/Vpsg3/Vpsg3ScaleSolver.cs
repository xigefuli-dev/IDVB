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
        var (pitchX, ratioX) = FindDominantPitch(projX, sc);

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
        Vpsg3SolverScratch scratch)
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

        var autocorr = scratch.AutocorrBuffer.AsSpan(0, maxLag - minLag);
        var bestLag = 0;
        var maxR = -1.0d;
        var rCount = 0;

        for (var lag = minLag; lag < maxLag; lag++)
        {
            var dot = 0.0d;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                dot += centered[i] * centered[i + lag];
            }

            var r = dot / variance;
            autocorr[rCount++] = Math.Abs(r);

            if (r > maxR)
            {
                maxR = r;
                bestLag = lag;
            }
        }

        if (rCount == 0 || maxR <= 0.05d) return (0.0d, 0.0d);

        // In-place sort of absolute autocorrelation values in scratch to find median without heap allocation
        autocorr.Sort();
        var medianR = autocorr[rCount / 2];
        var peakRatio = maxR / Math.Max(0.01d, medianR);

        return (bestLag, peakRatio);
    }
}
