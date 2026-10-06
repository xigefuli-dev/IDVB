using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Uniform, unrotated, positive-scale geometry for one supplied correspondence
/// triple. It neither generates correspondences nor decides map identity.
/// R(s), the MEC radius of Query-s*Source, is convex and is Lipschitz with
/// constant max|Source[i]-Source[0]| after translation-invariant centering.
/// </summary>
public static partial class MapFrontEntryFamilyGeometry
{
    public const double PhysicalTolerancePixels = 3d;
    private const int SearchSteps = 48;

    public static MapFrontEntryScaleSample EvaluateAtScale(
        IReadOnlyList<Point2d> source, IReadOnlyList<Point2d> query, double scale) =>
        Sample(CopyFrame(source, query), scale);

    public static MapFrontEntryFamilyResult Solve(
        IReadOnlyList<Point2d> source, IReadOnlyList<Point2d> query,
        double necessaryMinimumScale, double necessaryMaximumScale,
        CancellationToken cancellationToken = default, Func<bool>? budgetExpired = null)
    {
        var frame = CopyFrame(source, query);
        var requested = new MapFrontEntryScaleBracket(necessaryMinimumScale, necessaryMaximumScale);
        var disks = Array.AsReadOnly(frame.Source.Select((p, i) =>
            new MapFrontEntryTranslationDisk(p, frame.Query[i], PhysicalTolerancePixels)).ToArray());
        MapFrontEntryScaleBracket? possible = null, minimumBracket = null;
        var samples = new List<MapFrontEntryScaleSample>();
        MapFrontEntryScaleSample? best = null, representative = null;
        double? lipschitz = null, allowance = null;
        var boundaryLimited = false; var lowerOutside = false; var upperOutside = false;
        var zeroWidth = necessaryMinimumScale == necessaryMaximumScale;
        MapFrontEntryFamilyTermination? interruption = null;

        MapFrontEntryFamilyTermination? Interrupted() => interruption ??= cancellationToken.IsCancellationRequested
            ? MapFrontEntryFamilyTermination.Cancelled : budgetExpired?.Invoke() == true
                ? MapFrontEntryFamilyTermination.BudgetExpired : null;
        MapFrontEntryScaleSample Observe(double scale)
        {
            var sample = Sample(frame, scale); samples.Add(sample);
            if (sample.ArithmeticFinite && (best is null || sample.MecRadius < best.MecRadius)) best = sample;
            if (sample.OriginalPhysical3Verified && (representative is null
                || sample.OriginalPhysicalResiduals.Max() < representative.OriginalPhysicalResiduals.Max())) representative = sample;
            return sample;
        }
        double? LowerBound()
        {
            if (minimumBracket is not { } domain || lipschitz is not { } slope || allowance is not { } error) return null;
            double? result = null;
            foreach (var sample in samples)
            {
                if (sample.RadiusLowerBound is not { } radius) continue;
                var distance = Math.Max(Up(Math.Abs(sample.Scale - domain.Minimum)), Up(Math.Abs(sample.Scale - domain.Maximum)));
                var decrease = Up(slope * distance);
                var lower = Down(Down(radius - decrease) - error);
                if (double.IsFinite(lower)) result = Math.Max(result ?? 0, Math.Max(0, lower));
            }
            return result;
        }
        MapFrontEntryFamilyResult Finish(MapFrontEntryFamilyStatus status, MapFrontEntryFamilyTermination termination,
            bool discardLowerBound = false) => new()
        {
            Status = status, Termination = termination, RequestedScaleBracket = requested,
            PossibleScaleBracket = status == MapFrontEntryFamilyStatus.CertifiedInfeasible ? null : possible,
            MinimumSearchBracket = minimumBracket, Representative = representative, BestSample = best,
            ConservativeMinimumRadiusLowerBound = discardLowerBound ? null : LowerBound(), OriginalArithmeticAllowance = allowance,
            LowerOuterEndpointCertifiedOutside = lowerOutside, UpperOuterEndpointCertifiedOutside = upperOutside,
            BoundaryResolutionLimited = boundaryLimited, ZeroWidthInterval = zeroWidth,
            RepresentativeTouchesPhysicalBoundary = representative?.OriginalPhysicalResiduals.Any(r => r == PhysicalTolerancePixels) == true,
            Evaluations = samples.Count, OriginalDisks = disks
        };
        MapFrontEntryFamilyResult Stop(MapFrontEntryFamilyTermination reason) =>
            Finish(MapFrontEntryFamilyStatus.NumericallyUnresolved, reason);

        if (Interrupted() is { } before) return Stop(before);
        if (!frame.Finite || !double.IsFinite(necessaryMinimumScale) || !double.IsFinite(necessaryMaximumScale))
            return Stop(MapFrontEntryFamilyTermination.NonFiniteInput);
        if (necessaryMinimumScale > necessaryMaximumScale) return Stop(MapFrontEntryFamilyTermination.InvalidScaleInterval);
        if (necessaryMaximumScale <= 0) return Stop(MapFrontEntryFamilyTermination.NoPositiveScale);
        var minimum = Math.Max(0, necessaryMinimumScale); var maximum = necessaryMaximumScale;
        possible = minimumBracket = new(minimum, maximum);
        lipschitz = frame.LipschitzUpperBound(); allowance = ArithmeticAllowance(frame, possible.Value);
        Observe(minimum);
        if (maximum != minimum) Observe(maximum);
        if (minimum == maximum)
        {
            if (Interrupted() is { } stopped) return Stop(stopped);
            if (representative is not null)
            {
                if (LowerBound() is > PhysicalTolerancePixels)
                    return Finish(MapFrontEntryFamilyStatus.NumericallyUnresolved, MapFrontEntryFamilyTermination.NonFiniteArithmetic, true);
                return Finish(MapFrontEntryFamilyStatus.Feasible, MapFrontEntryFamilyTermination.FixedScale);
            }
            if (LowerBound() is > PhysicalTolerancePixels)
                return Finish(MapFrontEntryFamilyStatus.CertifiedInfeasible, MapFrontEntryFamilyTermination.FixedScale);
            boundaryLimited = true;
            return Stop(best is null || allowance is null || lipschitz is null || best.RadiusLowerBound is null
                || best.RadiusUpperBound is null ? MapFrontEntryFamilyTermination.NonFiniteArithmetic
                : MapFrontEntryFamilyTermination.ContactOrInsufficientLowerBound);
        }
        Observe(Interpolate(minimum, maximum, .5));
        var leastSquares = LeastSquaresScale(frame);
        if (leastSquares is { } seed && seed > 0 && seed >= minimum && seed <= maximum) Observe(seed);

        var lo = minimum; var hi = maximum;
        for (var step = 0; step < SearchSteps; step++)
        {
            if (Interrupted() is { } stopped) return Stop(stopped);
            var a = Interpolate(lo, hi, 1d / 3); var b = Interpolate(lo, hi, 2d / 3);
            if (!(a > lo && b > a && b < hi)) { boundaryLimited = true; break; }
            var left = Observe(a); var right = Observe(b);
            // A numerical radius comparison alone may discard the real
            // minimizer. Only disjoint conservative bounds can shrink it.
            if (left.RadiusLowerBound is { } leftLower && right.RadiusUpperBound is { } rightUpper && leftLower > rightUpper) lo = a;
            else if (right.RadiusLowerBound is { } rightLower && left.RadiusUpperBound is { } leftUpper && rightLower > leftUpper) hi = b;
            else { boundaryLimited = true; break; }
            minimumBracket = new(lo, hi);
        }
        if (Interrupted() is { } afterMinimum) return Stop(afterMinimum);
        Observe(Interpolate(lo, hi, .5));
        var lowerBound = LowerBound();
        if (representative is null)
        {
            if (lowerBound is > PhysicalTolerancePixels)
                return Finish(MapFrontEntryFamilyStatus.CertifiedInfeasible, MapFrontEntryFamilyTermination.Completed);
            boundaryLimited = true;
            return Stop(best is null || allowance is null || lipschitz is null || best.RadiusLowerBound is null
                || best.RadiusUpperBound is null ? MapFrontEntryFamilyTermination.NonFiniteArithmetic
                : MapFrontEntryFamilyTermination.ContactOrInsufficientLowerBound);
        }
        // Contradictory floating evidence must never become an empty-family
        // certificate, even if an arithmetic assumption was too optimistic.
        if (lowerBound is > PhysicalTolerancePixels)
            return Finish(MapFrontEntryFamilyStatus.NumericallyUnresolved, MapFrontEntryFamilyTermination.NonFiniteArithmetic, true);

        if (allowance is { } arithmetic)
        {
            var target = Up(PhysicalTolerancePixels + arithmetic);
            var anchor = representative;
            // The threshold here only broadens POSSIBLE geometry. It does not
            // admit a model: original Hypot<=3 was already verified above.
            if (anchor.RadiusUpperBound is { } anchorUpper && anchorUpper <= target)
            {
                var lower = RefineOuterBoundary(minimum, anchor.Scale, target);
                lowerOutside = lower.Outside;
                possible = new(lower.Scale, maximum);
                if (Interrupted() is { } leftStop) return Stop(leftStop);
                var upper = RefineOuterBoundary(maximum, anchor.Scale, target);
                upperOutside = upper.Outside;
                possible = new(lower.Scale, upper.Scale);
                if (Interrupted() is { } rightStop) return Stop(rightStop);
            }
            else boundaryLimited = true;
        }
        else boundaryLimited = true;
        return Finish(MapFrontEntryFamilyStatus.Feasible, MapFrontEntryFamilyTermination.Completed);

        (double Scale, bool Outside) RefineOuterBoundary(double outer, double inner, double target)
        {
            if (outer == inner) return (outer, false);
            var endpoint = Observe(outer);
            if (endpoint.RadiusLowerBound is not { } endpointLower || endpointLower <= target) return (outer, false);
            for (var step = 0; step < SearchSteps; step++)
            {
                if (Interrupted() is not null) return (outer, true);
                var midpoint = Interpolate(Math.Min(outer, inner), Math.Max(outer, inner), .5);
                if (midpoint == outer || midpoint == inner) { boundaryLimited = true; break; }
                var sample = Observe(midpoint);
                if (sample.RadiusLowerBound is { } lower && lower > target) outer = midpoint;
                else if (sample.RadiusUpperBound is { } upper && upper <= target) inner = midpoint;
                else { boundaryLimited = true; break; }
            }
            // Preserve the OUTSIDE endpoint: returning the inside sample would
            // falsely remove unsampled feasible scales near the boundary.
            return (outer, true);
        }
    }

    private static Frame CopyFrame(IReadOnlyList<Point2d> source, IReadOnlyList<Point2d> query)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(query);
        if (source.Count != 3 || query.Count != 3) throw new ArgumentException("Exactly three source/query correspondences are required.");
        return new(source.ToArray(), query.ToArray());
    }
    private static double Interpolate(double lower, double upper, double fraction) =>
        lower * (1 - fraction) + upper * fraction;
    private static double? LeastSquaresScale(Frame frame)
    {
        var p = frame.Source.Select(v => new Point2d(v.X - frame.Source[0].X, v.Y - frame.Source[0].Y)).ToArray();
        var q = frame.Query.Select(v => new Point2d(v.X - frame.Query[0].X, v.Y - frame.Query[0].Y)).ToArray();
        var pm = new Point2d(p[1].X / 3 + p[2].X / 3, p[1].Y / 3 + p[2].Y / 3);
        var qm = new Point2d(q[1].X / 3 + q[2].X / 3, q[1].Y / 3 + q[2].Y / 3);
        double numerator = 0, denominator = 0;
        for (var i = 0; i < 3; i++)
        {
            var x = p[i].X - pm.X; var y = p[i].Y - pm.Y;
            numerator += x * (q[i].X - qm.X) + y * (q[i].Y - qm.Y); denominator += x * x + y * y;
        }
        var scale = numerator / denominator;
        return denominator > 0 && double.IsFinite(scale) ? scale : null;
    }
}
