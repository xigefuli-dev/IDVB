using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public enum ScanIdentityState { Unverified, Excluded, Supported }
public readonly record struct ScanConflictPoint(int X, int Y);
internal enum ScanIdentitySelectionPolicy
{
    RequireUniqueSupport,
    AllowDominantSupport
}

public sealed record ScanIdentityEvidence(ScanIdentityState State, int TestedPoints,
    int TotalPoints, double ForwardMeanPixels, double SupportedFraction,
    double LongestConflictPixels, string Reason)
{
    public int UnknownReferencePoints { get; init; }
    public int ConflictCell { get; init; } = -1;
    public int ConflictCellPoints { get; init; }
    public int ConflictCellHits { get; init; }
    public ScanConflictPoint? ConflictStart { get; init; }
    public ScanConflictPoint? ConflictEnd { get; init; }
    public double EvaluatedScale { get; init; }
    public double ViewportOffsetX { get; init; }
    public double ViewportOffsetY { get; init; }
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
    private readonly Rect _unknownBounds;
    // Candidate views share immutable distance storage and retain only this
    // floor's explicitly erased anchor rectangle, evaluated at each candidate pose.
    private ScanStructureIndex(ScanStructureIndex source, Rect unknownBounds)
    {
        Width = source.Width; Height = source.Height;
        _distances = source._distances;
        _unknownBounds = unknownBounds.Intersect(new(0, 0, Width, Height));
    }
    internal ScanStructureIndex WithUnknownBounds(Rect bounds) => new(this, bounds);
    internal ScanStructureIndex WithScanAnchor(MapRecord map, string floor)
    {
        var bounds = MapScanFloorRules.GetScanFeatureAnchor(map, floor)?.Bounds;
        // Exactly the rectangle erased by BuildSideEntranceFeatureCache.
        return bounds?.IsValid == true ? WithUnknownBounds(new(
            (int)Math.Floor(bounds.X * Width), (int)Math.Floor(bounds.Y * Height),
            (int)Math.Ceiling(bounds.Width * Width), (int)Math.Ceiling(bounds.Height * Height))) : this;
    }
    internal bool IsUnknown(double x, double y) => _unknownBounds.Width > 0 && _unknownBounds.Height > 0
        && x >= _unknownBounds.X && y >= _unknownBounds.Y
        && x < _unknownBounds.Right && y < _unknownBounds.Bottom;
    internal static bool HasEnoughKnownPoints(int known, int total, int minimum) =>
        known >= minimum && known >= total * .65;
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
    // DeepScan evaluates many local poses. Decode its immutable byte distances
    // once instead of repeating four piecewise decodes for every sampled pixel.
    // Ordinary modes and the final identity verifier retain their original path.
    private static class DeepScanDecoder
    {
        internal static readonly double[] Values = Enumerable.Range(0, 256)
            .Select(value => DecodeDistance((byte)value)).ToArray();
    }
    internal double DeepScanDistance(double x, double y, double scale)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= Width - 1 || y >= Height - 1)
            return 50d;
        var ix = (int)x; var iy = (int)y;
        var fx = x - ix; var fy = y - iy;
        var lookup = DeepScanDecoder.Values;
        var row = iy * Width + ix;
        var distance = (lookup[_distances[row]] * (1 - fx)
                + lookup[_distances[row + 1]] * fx) * (1 - fy)
            + (lookup[_distances[row + Width]] * (1 - fx)
                + lookup[_distances[row + Width + 1]] * fx) * fy;
        return distance * scale;
    }
    public double Score(IReadOnlyList<Point> points, double scale, double x, double y)
    {
        var sum = 0d;
        var known = 0;
        foreach (var p in points)
        {
            var rx = (p.X - x) / scale;
            var ry = (p.Y - y) / scale;
            if (IsUnknown(rx, ry)) continue;
            var d = Distance(rx, ry, scale);
            known++;
            sum += d <= .8 ? 1 : d <= 2.5 ? .7 : d <= 5.5 ? .4 : 0;
        }
        return known > 0 && HasEnoughKnownPoints(known, points.Count, Math.Min(80, points.Count))
            ? sum / known : 0;
    }
}

internal static class ScanIdentityVerifier
{
    internal readonly record struct SelectionDecision(Guid? MapId, string Reason);
    public static Guid? SelectIdentity(IReadOnlyList<SideEntranceScanCandidate> candidates,
        bool retrievalComplete, bool withinBudget, IReadOnlyList<Guid[]>? variantGroups = null,
        ScanIdentitySelectionPolicy selectionPolicy = ScanIdentitySelectionPolicy.RequireUniqueSupport)
        => EvaluateSelection(candidates, retrievalComplete, withinBudget, variantGroups, selectionPolicy).MapId;

    internal static SelectionDecision EvaluateSelection(IReadOnlyList<SideEntranceScanCandidate> candidates,
        bool retrievalComplete, bool withinBudget, IReadOnlyList<Guid[]>? variantGroups = null,
        ScanIdentitySelectionPolicy selectionPolicy = ScanIdentitySelectionPolicy.RequireUniqueSupport)
    {
        if (!retrievalComplete) return new(null, "retrieval-incomplete");
        if (!withinBudget) return new(null, "execution-unavailable");
        if (candidates.Any(c => c.IdentityEvidence.State == ScanIdentityState.Unverified))
            return new(null, "unverified-identities");
        var supported = candidates.Where(c => c.IdentityEvidence.State == ScanIdentityState.Supported)
            .OrderBy(c => FitCost(c.IdentityEvidence))
            .ThenByDescending(c => c.IdentityEvidence.SupportedFraction)
            .ThenBy(c => c.Map.SequenceNumber).ThenBy(c => c.Map.Id).ToArray();
        var verified = supported.Where(c => c.Disposition == SideEntranceCandidateDisposition.Reliable).ToArray();
        if (verified.Length == 0) return new(null, supported.Length == 0
            ? "all-identities-excluded" : "supported-without-confirmed-alignment");
        // Compare identities at the user-selectable family boundary. Only the
        // selected member needs a confirmed alignment; a fully compared losing
        // identity must not force a dialog merely because it was not aligned.
        var winnerId = verified[0].Map.Id;
        var winnerCost = FitCost(verified[0].IdentityEvidence);
        // Do not transitively merge overlapping groups. Each declared family has
        // to beat every supported identity outside that one family independently.
        var families = (variantGroups ?? []).Where(group => group.Contains(winnerId))
            .Append(new[] { winnerId });
        var selected = families.Any(family => supported.Where(c => !family.Contains(c.Map.Id)).All(c =>
            selectionPolicy == ScanIdentitySelectionPolicy.AllowDominantSupport
            && FitCost(c.IdentityEvidence) - winnerCost >= DominantFitMargin));
        return new(selected ? winnerId : null, selected ? "selected" : "competing-supported-identities");
    }
    internal const double DominantFitMargin = .35;
    internal static double FitCost(ScanIdentityEvidence evidence) =>
        evidence.ForwardMeanPixels + (1 - evidence.SupportedFraction) * 5;
    internal static bool CannotBeatFitCost(double partialDistance, int unsupportedPoints,
        int totalPoints, double competitiveCost) => totalPoints > 0
        && (partialDistance + unsupportedPoints * 5d) / totalPoints >= competitiveCost;
    // Same safety policy in every mode. Distances are measured in screen pixels.
    internal const double SupportTolerancePixels = 5.5;
    internal const double MinimumSupport = .88;
    internal const double MaximumContinuousConflictPixels = 30;
    internal static bool HasRefinablePose(ScanIdentityEvidence evidence) =>
        evidence.State == ScanIdentityState.Excluded && evidence.TestedPoints >= 80
        && evidence.SupportedFraction >= .80 && double.IsFinite(evidence.ForwardMeanPixels);

    internal static bool ShouldAttemptStructureRegistration(ScanIdentityEvidence evidence, ScanPerformanceMode mode) =>
        evidence.State == ScanIdentityState.Supported
        || (mode != ScanPerformanceMode.DeepScan && HasRefinablePose(evidence));

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
        var deep = policy.Mode == ScanPerformanceMode.DeepScan;
        double SampleDistance(double x, double y) => deep ? index.DeepScanDistance(x, y, scale) : index.Distance(x, y, scale);
        bool Unknown(Point p) => index.IsUnknown((p.X - tx) / scale, (p.Y - ty) / scale);
        // Unknown breaks a conflict run, but is never counted as a supporting hit.
        double Distance(Point p) => Unknown(p) ? 0 : SampleDistance((p.X - tx) / scale, (p.Y - ty) / scale);
        var hits = 0; var tested = 0; var unknown = 0; var distance = 0d;
        Span<int> cellTotals = stackalloc int[16];
        Span<int> cellHits = stackalloc int[16];
        foreach (var p in points)
        {
            if (((tested + unknown) & 255) == 0 && context is { CanCompute: false })
                return ScanIdentityEvidence.Unverified("deadline");
            var rx = (p.X - tx) / scale;
            var ry = (p.Y - ty) / scale;
            if (index.IsUnknown(rx, ry)) { unknown++; continue; }
            var d = SampleDistance(rx, ry);
            var cell = Math.Min(3, p.X * 4 / frame.Source.Width) + 4 * Math.Min(3, p.Y * 4 / frame.Source.Height);
            cellTotals[cell]++;
            if (d <= SupportTolerancePixels) cellHits[cell]++;
            distance += d;
            if (d <= SupportTolerancePixels) hits++;
            tested++;
            // Conservative upper bound: even if every remaining pixel matches this transform cannot pass.
            if (context?.Policy.Mode is not (ScanPerformanceMode.Quality or ScanPerformanceMode.DeepScan)
                // Do not exclude an identity before enough known reference is
                // established: remaining points may all lie in its erased anchor.
                && ScanStructureIndex.HasEnoughKnownPoints(tested, points.Length, 80)
                && hits + points.Length - tested - unknown < MinimumSupport * (points.Length - unknown))
                return new(ScanIdentityState.Excluded, tested, points.Length, distance / tested,
                    hits / (double)tested, 0, "unexplained-visible-structure")
                    { UnknownReferencePoints = unknown };
        }
        if (!ScanStructureIndex.HasEnoughKnownPoints(tested, points.Length, 80))
            return ScanIdentityEvidence.Unverified("insufficient-unmasked-reference-structure")
                with { TestedPoints = tested, TotalPoints = points.Length, UnknownReferencePoints = unknown };
        var longest = 0d;
        Point? conflictStart = null, conflictEnd = null;
        var prepared = deep ? frame.PreparedConflictContours : null;
        for (var i = 0; i < frame.Contours.Count; i++)
        {
            if (context is { CanCompute: false }) return ScanIdentityEvidence.Unverified("deadline");
            var measured = MeasureStraightConflict(frame.Contours[i], Distance, out var measuredStart, out var measuredEnd,
                preparedVertices: prepared?[i]);
            if (measured > longest) { longest = measured; conflictStart = measuredStart; conflictEnd = measuredEnd; }
            if (longest >= MaximumContinuousConflictPixels) break;
        }
        var support = hits / (double)tested;
        var spatialConflict = false;
        var conflictCell = -1;
        for (var cell = 0; cell < cellTotals.Length; cell++)
            if (cellTotals[cell] >= 30 && cellHits[cell] < cellTotals[cell] * .70)
            { spatialConflict = true; if (conflictCell < 0) conflictCell = cell; }
        var accepted = support >= MinimumSupport && !spatialConflict && longest < MaximumContinuousConflictPixels;
        return new(accepted ? ScanIdentityState.Supported : ScanIdentityState.Excluded,
            tested, points.Length, distance / tested, support, longest,
            accepted ? "visible-structure-supported" : spatialConflict ? "spatial-support-conflict" : "visible-contour-conflict")
        {
            UnknownReferencePoints = unknown,
            ConflictCell = conflictCell,
            ConflictCellPoints = conflictCell < 0 ? 0 : cellTotals[conflictCell],
            ConflictCellHits = conflictCell < 0 ? 0 : cellHits[conflictCell],
            ConflictStart = conflictStart is { } startPoint ? new(startPoint.X, startPoint.Y) : null,
            ConflictEnd = conflictEnd is { } endPoint ? new(endPoint.X, endPoint.Y) : null,
            EvaluatedScale = scale, ViewportOffsetX = tx, ViewportOffsetY = ty
        };
    }

    internal static double MeasureStraightConflict(Point[] contour, Func<Point, double> distance)
        => MeasureStraightConflict(contour, distance, out _, out _);

    internal static double MeasureStraightConflict(Point[] contour, Func<Point, double> distance,
        out Point? conflictStart, out Point? conflictEnd, Point[]? preparedVertices = null)
    {
        conflictStart = null; conflictEnd = null;
        // A small badge's perimeter (or a contour visited twice) is not the length
        // of an unexplained wall. Split at genuine turns, tolerating raster stair
        // steps, and measure supported/unsupported runs on each straight segment.
        if (contour.Length == 0) return 0;
        var vertices = preparedVertices ?? Cv2.ApproxPolyDP(contour, 1.5, true);
        if (vertices.Length < 2) return 0;
        // Approximation identifies turns only. Rasterizing its chords invents
        // pixels outside the observed contour and can turn a fully supported
        // wall into a long conflict near the distance tolerance boundary.
        var cursor = Array.IndexOf(contour, vertices[0]);
        if (cursor < 0) throw new ArgumentException("Conflict vertices must belong to the observed contour.", nameof(preparedVertices));
        var longest = 0d;
        for (var i = 0; i < vertices.Length; i++)
        {
            var end = vertices[(i + 1) % vertices.Length];
            Point? runStart = null;
            var previous = contour[cursor];
            for (var visited = 0; visited <= contour.Length; visited++)
            {
                var p = contour[cursor];
                if (distance(p) > SupportTolerancePixels)
                {
                    runStart ??= previous;
                    var dx = p.X - runStart.Value.X; var dy = p.Y - runStart.Value.Y;
                    var run = Math.Sqrt((double)dx * dx + (double)dy * dy);
                    if (run > longest) { longest = run; conflictStart = runStart; conflictEnd = p; }
                    if (longest >= MaximumContinuousConflictPixels) return longest;
                }
                else runStart = null;
                if (p == end) break;
                previous = p;
                cursor = (cursor + 1) % contour.Length;
            }
        }
        return longest;
    }
}
