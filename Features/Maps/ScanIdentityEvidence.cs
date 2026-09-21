using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public enum ScanIdentityState { Unverified, Excluded, Supported }
public sealed record ScanIdentityEvidence(ScanIdentityState State, int TestedPoints,
    int TotalPoints, double ForwardMeanPixels, double SupportedFraction,
    double LongestConflictPixels, string Reason)
{
    public static ScanIdentityEvidence Unverified(string reason) =>
        new(ScanIdentityState.Unverified, 0, 0, double.NaN, 0, 0, reason);
}

/// <summary>Managed immutable full-floor distance index; never a cropped template.
/// Lifetime is tied to the catalog Mat, including its map/floor/anchor revision.</summary>
internal sealed class ScanStructureIndex
{
    private static readonly ConditionalWeakTable<Mat, ScanStructureIndex> Cache = new();
    public int Width { get; }
    public int Height { get; }
    private readonly float[] _distances;
    private ScanStructureIndex(Mat line)
    {
        Width = line.Width; Height = line.Height;
        using var inverse = new Mat();
        Cv2.Threshold(line, inverse, 128, 255, ThresholdTypes.BinaryInv);
        using var distances = new Mat();
        Cv2.DistanceTransform(inverse, distances, DistanceTypes.L2, DistanceTransformMasks.Precise);
        _distances = new float[Width * Height];
        Marshal.Copy(distances.Data, _distances, 0, _distances.Length);
    }
    public static ScanStructureIndex Get(Mat line) => Cache.GetValue(line, m => new(m));
    public double Distance(double x, double y, double scale)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= Width - 1 || y >= Height - 1)
            return 50d;
        var ix = (int)x; var iy = (int)y;
        var fx = x - ix; var fy = y - iy;
        return ((_distances[iy * Width + ix] * (1 - fx) + _distances[iy * Width + ix + 1] * fx) * (1 - fy)
            + (_distances[(iy + 1) * Width + ix] * (1 - fx) + _distances[(iy + 1) * Width + ix + 1] * fx) * fy) * scale;
    }
    public double Score(IReadOnlyList<Point> points, double scale, double x, double y)
    {
        var sum = 0d;
        foreach (var p in points)
        {
            var d = Distance((p.X - x) / scale, (p.Y - y) / scale, scale);
            sum += d <= .8 ? 1 : d <= 2.5 ? .7 : d <= 5.5 ? .4 : 0;
        }
        return points.Count == 0 ? 0 : sum / points.Count;
    }
}

internal static class ScanIdentityVerifier
{
    public static Guid? SelectIdentity(IReadOnlyList<SideEntranceScanCandidate> candidates,
        bool retrievalComplete, bool withinBudget)
    {
        if (!retrievalComplete || !withinBudget || candidates.Any(c => c.IdentityEvidence.State == ScanIdentityState.Unverified))
            return null;
        var supported = candidates.Where(c => c.IdentityEvidence.State == ScanIdentityState.Supported
                && c.Disposition == SideEntranceCandidateDisposition.Reliable)
            .OrderBy(c => FitCost(c.IdentityEvidence)).ToArray();
        if (supported.Length == 0) return null;
        // The class is a closed set. Compare actual full-frame fit, not single-map aperture margin.
        if (supported.Length > 1 && FitCost(supported[1].IdentityEvidence) - FitCost(supported[0].IdentityEvidence) < .35)
            return null;
        return supported[0].Map.Id;
    }
    private static double FitCost(ScanIdentityEvidence evidence) =>
        evidence.ForwardMeanPixels + (1 - evidence.SupportedFraction) * 5;
    // Same safety policy in every mode. Distances are measured in screen pixels.
    internal const double SupportTolerancePixels = 5.5;
    internal const double MinimumSupport = .88;
    internal const double MaximumContinuousConflictPixels = 30;
    public static ScanIdentityEvidence Verify(ScanFrameEvidence frame, ScanStructureIndex index,
        MapOverlayTransform transform, MapScreenRect viewport, ScanExecutionContext? context)
    {
        var policy = context?.Policy ?? ScanExecutionPolicy.For(ScanPerformanceMode.Balanced);
        if (!double.IsFinite(transform.ScaleX) || transform.ScaleX < policy.MinimumScale
            || transform.ScaleX > policy.MaximumScale || !double.IsFinite(transform.ScaleY)
            || Math.Abs(transform.ScaleX - transform.ScaleY) > .000001
            || !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY))
            return ScanIdentityEvidence.Unverified("invalid-transform");
        var points = frame.DensePoints;
        if (points.Length < 80 || frame.Contours.Count == 0)
            return ScanIdentityEvidence.Unverified("insufficient-visible-structure");
        var scale = transform.ScaleX;
        var tx = transform.OffsetX - viewport.X;
        var ty = transform.OffsetY - viewport.Y;
        double Distance(Point p) => index.Distance((p.X - tx) / scale, (p.Y - ty) / scale, scale);
        var hits = 0; var tested = 0; var distance = 0d;
        Span<int> cellTotals = stackalloc int[16];
        Span<int> cellHits = stackalloc int[16];
        foreach (var p in points)
        {
            if ((tested & 255) == 0 && context is { CanCompute: false })
                return ScanIdentityEvidence.Unverified("deadline");
            var d = Distance(p);
            var cell = Math.Min(3, p.X * 4 / frame.Source.Width) + 4 * Math.Min(3, p.Y * 4 / frame.Source.Height);
            cellTotals[cell]++;
            if (d <= SupportTolerancePixels) cellHits[cell]++;
            distance += d;
            if (d <= SupportTolerancePixels) hits++;
            tested++;
            // Conservative upper bound: even if every remaining pixel matches this transform cannot pass.
            if (context?.Policy.Mode != ScanPerformanceMode.Quality
                && hits + points.Length - tested < MinimumSupport * points.Length)
                return new(ScanIdentityState.Excluded, tested, points.Length, distance / tested,
                    hits / (double)tested, 0, "unexplained-visible-structure");
        }
        var longest = 0d;
        foreach (var contour in frame.Contours)
        {
            if (context is { CanCompute: false }) return ScanIdentityEvidence.Unverified("deadline");
            var run = 0d;
            // Walk twice so a conflict straddling a closed contour's origin is not hidden.
            for (var i = 1; i < contour.Length * 2; i++)
            {
                var p = contour[i % contour.Length];
                var previous = contour[(i - 1) % contour.Length];
                if (Distance(p) > SupportTolerancePixels)
                    run += Math.Sqrt(Math.Pow(p.X - previous.X, 2) + Math.Pow(p.Y - previous.Y, 2));
                else run = 0;
                longest = Math.Max(longest, run);
                if (longest >= MaximumContinuousConflictPixels) break;
            }
            if (longest >= MaximumContinuousConflictPixels) break;
        }
        var support = hits / (double)points.Length;
        var spatialConflict = false;
        for (var cell = 0; cell < cellTotals.Length; cell++)
            if (cellTotals[cell] >= 30 && cellHits[cell] < cellTotals[cell] * .70) spatialConflict = true;
        var accepted = support >= MinimumSupport && !spatialConflict && longest < MaximumContinuousConflictPixels;
        return new(accepted ? ScanIdentityState.Supported : ScanIdentityState.Excluded,
            tested, points.Length, distance / tested, support, longest,
            accepted ? "visible-structure-supported" : spatialConflict ? "spatial-support-conflict" : "visible-contour-conflict");
    }
}
