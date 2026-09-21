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
    // Identity verification only distinguishes sub-pixel support bands up to
    // 5.5 px. Keeping an IEEE float for every authored-map pixel made the
    // startup catalog cache consume roughly four bytes per pixel (about 1 GB
    // for the current catalog). A piecewise fixed-point byte keeps 1/16 px
    // precision through 8 px, then 1/2 px precision through 71.5 px. This
    // preserves all support boundaries across the configured 0.1x..5x scale
    // range while capping retained storage at one byte per reference pixel.
    private const double FineDistanceLimit = 8d;
    private const double FineDistanceUnitsPerPixel = 16d;
    private const double CoarseDistanceUnitsPerPixel = 2d;
    private const double CoarseDistanceOffset = 112d;
    private static readonly ConditionalWeakTable<Mat, ScanStructureIndex> Cache = new();
    public int Width { get; }
    public int Height { get; }
    internal int RetainedDistanceBytes => _distances.Length;
    private readonly byte[] _distances;
    private ScanStructureIndex(Mat line)
    {
        Width = line.Width; Height = line.Height;
        using var inverse = new Mat();
        Cv2.Threshold(line, inverse, 128, 255, ThresholdTypes.BinaryInv);
        using var distances = new Mat();
        Cv2.DistanceTransform(inverse, distances, DistanceTypes.L2, DistanceTransformMasks.Precise);
        using var encodedDistances = new Mat();
        distances.ConvertTo(
            encodedDistances,
            MatType.CV_8UC1,
            FineDistanceUnitsPerPixel);
        using var coarseDistances = new Mat();
        distances.ConvertTo(
            coarseDistances,
            MatType.CV_8UC1,
            CoarseDistanceUnitsPerPixel,
            CoarseDistanceOffset);
        using var coarseMask = new Mat();
        Cv2.Compare(distances, FineDistanceLimit, coarseMask, CmpTypes.GT);
        coarseDistances.CopyTo(encodedDistances, coarseMask);
        _distances = new byte[Width * Height];
        Marshal.Copy(encodedDistances.Data, _distances, 0, _distances.Length);
    }
    public static ScanStructureIndex Get(Mat line) => Cache.GetValue(line, m => new(m));
    public double Distance(double x, double y, double scale)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= Width - 1 || y >= Height - 1)
            return 50d;
        var ix = (int)x; var iy = (int)y;
        var fx = x - ix; var fy = y - iy;
        var distance = (DecodeDistance(_distances[iy * Width + ix]) * (1 - fx)
                + DecodeDistance(_distances[iy * Width + ix + 1]) * fx) * (1 - fy)
            + (DecodeDistance(_distances[(iy + 1) * Width + ix]) * (1 - fx)
                + DecodeDistance(_distances[(iy + 1) * Width + ix + 1]) * fx) * fy;
        return distance * scale;
    }

    private static double DecodeDistance(byte encoded) =>
        encoded <= FineDistanceLimit * FineDistanceUnitsPerPixel
            ? encoded / FineDistanceUnitsPerPixel
            : (encoded - CoarseDistanceOffset) / CoarseDistanceUnitsPerPixel;
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
        bool retrievalComplete, bool withinBudget, IReadOnlyList<Guid[]>? variantGroups = null)
    {
        if (!retrievalComplete || !withinBudget || candidates.Any(c => c.IdentityEvidence.State == ScanIdentityState.Unverified))
            return null;
        var supported = candidates.Where(c => c.IdentityEvidence.State == ScanIdentityState.Supported
                && c.Disposition == SideEntranceCandidateDisposition.Reliable)
            .OrderBy(c => FitCost(c.IdentityEvidence)).ToArray();
        if (supported.Length == 0) return null;
        // A local contour veto is not evidence that a near-identical sibling is absent.
        // Icons and reference omissions can trigger that veto even at >97% full-frame
        // support. Keep the declared variant group unresolved instead of letting the
        // first sibling just below the contour threshold win by elimination.
        var winnerId = supported[0].Map.Id;
        foreach (var group in variantGroups ?? [])
        {
            if (!group.Contains(winnerId)) continue;
            foreach (var sibling in candidates.Where(c => c.Map.Id != winnerId && group.Contains(c.Map.Id)))
            {
                // Mean distance can improve by fitting a shared room more tightly
                // while explaining less of the visible structure. That trade-off
                // cannot establish which near-identical variant is present.
                if (sibling.IdentityEvidence.State == ScanIdentityState.Supported
                    && sibling.IdentityEvidence.SupportedFraction > supported[0].IdentityEvidence.SupportedFraction)
                    return null;
                var evidence = sibling.SearchHypotheses.Count > 0
                    ? sibling.SearchHypotheses.Select(h => h.IdentityEvidence)
                    : [sibling.IdentityEvidence];
                if (evidence.Any(e => e.State == ScanIdentityState.Excluded
                    && e.TestedPoints == e.TotalPoints && e.TotalPoints > 0
                    && e.SupportedFraction >= MinimumSupport
                    && e.Reason is "visible-contour-conflict" or "spatial-support-conflict"))
                    return null;
            }
        }
        // The class is a closed set. Compare actual full-frame fit, not single-map aperture margin.
        if (supported.Length > 1 && FitCost(supported[1].IdentityEvidence) - FitCost(supported[0].IdentityEvidence) < .35)
            return null;
        return winnerId;
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
            longest = Math.Max(longest, MeasureStraightConflict(contour, Distance));
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

    internal static double MeasureStraightConflict(Point[] contour, Func<Point, double> distance)
    {
        // A small badge's perimeter (or a contour visited twice) is not the length
        // of an unexplained wall. Split at genuine turns, tolerating raster stair
        // steps, and measure supported/unsupported runs on each straight segment.
        var vertices = Cv2.ApproxPolyDP(contour, 1.5, true);
        var longest = 0d;
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (steps == 0) continue;
            var run = 0d;
            for (var step = 0; step <= steps; step++)
            {
                var p = new Point((int)Math.Round(a.X + dx * (step / (double)steps)),
                    (int)Math.Round(a.Y + dy * (step / (double)steps)));
                if (distance(p) > SupportTolerancePixels)
                    run += step == 0 ? 0 : length / steps;
                else run = 0;
                longest = Math.Max(longest, run);
                if (longest >= MaximumContinuousConflictPixels) return longest;
            }
        }
        return longest;
    }
}
