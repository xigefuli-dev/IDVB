using OpenCvSharp;
using IDVBuff.Pipeline;
using MapLocalCorner = IDVBuff.Features.Maps.MapLocalCornerGeometry;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    // One instance belongs to one Solve call and one source/query corner pair.
    // Keys are exact correspondence identities, never rounded poses or scales.
    private sealed partial class CornerFitMemo
    {
        internal readonly Dictionary<string, IReadOnlyList<CornerFitPose>> SixMatches = [];
        internal readonly Dictionary<string, IReadOnlyList<CornerFitPose>> TentativeSets = [];
        internal readonly Dictionary<string, CornerFitPose?> Triplets = [];
        internal readonly Dictionary<(double Scale, double X, double Y),
            (int Count, double Error)> MeasuredPoses = [];
        internal bool HasUncertifiedFits { get; set; }
    }

    private readonly record struct CornerFitPose(
        double Scale, double X, double Y, int Count, double Error);
    private readonly record struct CornerScaleInterval(double Minimum, double Maximum);
    private readonly record struct CornerCircleFit(double Scale, Point2d Translation, double Radius);

    private static IEnumerable<(double Scale, double X, double Y, int Count, double Error)>
        FitMeasuredCornerProposals(IReadOnlyList<MapLocalCorner> source,
            IReadOnlyList<MapLocalCorner> query, Dictionary<(int X, int Y), int[]> queryCells,
            double scale, Point2d translation, Point2d pairCenter, double pairLength,
            int sourceA, int queryA, int sourceB, int queryB,
            CornerFitMemo memo, CancellationToken cancellationToken)
    {
        // The pair prediction contributes e + 2e*r/L, and the third measured
        // query point contributes another e. This is proposal collection only.
        var tentative = FindCornerMatches(source, query, queryCells, scale, translation,
            point => CornerMatchTolerance * (2 + 2 * Distance(point, pairCenter) / pairLength),
            cancellationToken, independentOnly: false);
        tentative.Sort((a, b) => a.Source != b.Source
            ? a.Source.CompareTo(b.Source) : a.Query.CompareTo(b.Query));
        var tentativeKey = $"{sourceA}:{queryA};{sourceB}:{queryB}|{CornerCorrespondenceKey(tentative)}";
        if (!memo.TentativeSets.TryGetValue(tentativeKey, out var proposals))
        {
            var collected = FitPairThirdCornerProposals(source, query, queryCells,
                tentative, sourceA, queryA, sourceB, queryB, memo, cancellationToken);
            // This is a finite proposal policy: visit every third correspondence
            // for this generating pair, preserving every measured model. A
            // recovered six-corner model does not prove that no other continuous
            // pose or MEC-only runner exists in this uncertainty band.
            if (collected.Count == 0 && tentative.Count >= 6)
            {
                using var sixFallback = MapOperationTraceAmbient.StartChild(
                    "geometry_six_corner_fallback", MapOperationWaitKind.Compute,
                    route: $"tentative={tentative.Count}/source={source.Count}/query={query.Count}");
                var diagnostics = SixCornerFitDiagnostics.Start(memo.CurrentGeneratingGateContext);
                try
                {
                    CollectSixCornerFitFallback(source, query, queryCells, tentative,
                        memo, collected, cancellationToken, diagnostics);
                    diagnostics?.CompleteEnumeration();
                }
                finally
                {
                    diagnostics?.Write(source, query, tentative, sourceA, queryA,
                        sourceB, queryB, scale, translation);
                }
            }
            CheckGeometryBudget(cancellationToken);
            proposals = collected;
            memo.TentativeSets.Add(tentativeKey, proposals);
        }
        foreach (var proposal in proposals)
        {
            CheckGeometryBudget(cancellationToken);
            memo.SearchDiagnostics?.RecordFitMeasuredYield();
            yield return (proposal.Scale, proposal.X, proposal.Y, proposal.Count, proposal.Error);
        }
    }

    private static List<CornerFitPose> FitPairThirdCornerProposals(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        IReadOnlyList<(int Source, int Query, double Error)> tentative,
        int sourceA, int queryA, int sourceB, int queryB,
        CornerFitMemo memo, CancellationToken cancellationToken)
    {
        var cfg = Vpsg3TuningConfig.Default;
        var result = new List<CornerFitPose>();
        var seen = new HashSet<(double Scale, double X, double Y)>();
        foreach (var third in tentative)
        {
            CheckGeometryBudget(cancellationToken);
            if (third.Source == sourceA || third.Source == sourceB
                || third.Query == queryA || third.Query == queryB) continue;
            // Canonical exact correspondence keys reuse work across weak pairs;
            // close poses alone cannot replace different measurement bands.
            var correspondence = new[] { (Source: sourceA, Query: queryA),
                (Source: sourceB, Query: queryB), (third.Source, third.Query) }
                .OrderBy(pair => pair.Source).ThenBy(pair => pair.Query).ToArray();
            var key = string.Join(";", correspondence.Select(pair => $"{pair.Source}:{pair.Query}"));
            if (memo.Triplets.TryGetValue(key, out var cached))
            {
                if (cached is { } fit && seen.Add((fit.Scale, fit.X, fit.Y))) result.Add(fit);
                continue;
            }
            // Null means this exact finite proposal was measured and rejected.
            // Cancellation/budget failure leaves the whole Solve incomplete.
            memo.Triplets.Add(key, null);
            var pa = source[correspondence[0].Source].Point;
            var pb = source[correspondence[1].Source].Point;
            var pc = source[correspondence[2].Source].Point;
            var qa = query[correspondence[0].Query].Point;
            var qb = query[correspondence[1].Query].Point;
            var qc = query[correspondence[2].Query].Point;
            var sourceCenter = (pa + pb + pc) * (1d / 3d);
            var queryCenter = (qa + qb + qc) * (1d / 3d);
            var da = pa - sourceCenter;
            var db = pb - sourceCenter;
            var dc = pc - sourceCenter;
            var denominator = Dot(da, da) + Dot(db, db) + Dot(dc, dc);
            if (denominator <= 0) continue;
            var fittedScale = (Dot(da, qa - queryCenter) + Dot(db, qb - queryCenter)
                + Dot(dc, qc - queryCenter)) / denominator;
            if (!double.IsFinite(fittedScale) || fittedScale < cfg.MinSupportedScale
                || fittedScale > cfg.MaxSupportedScale) continue;
            var fittedTranslation = queryCenter - sourceCenter * fittedScale;
            if (!double.IsFinite(fittedTranslation.X) || !double.IsFinite(fittedTranslation.Y)) continue;
            // A triplet proposes a pose only when its measured correspondences
            // meet the same physical tolerance. LS itself is not acceptance.
            if (Distance(pa * fittedScale + fittedTranslation, qa) > CornerMatchTolerance
                || Distance(pb * fittedScale + fittedTranslation, qb) > CornerMatchTolerance
                || Distance(pc * fittedScale + fittedTranslation, qc) > CornerMatchTolerance) continue;
            var poseKey = (fittedScale, fittedTranslation.X, fittedTranslation.Y);
            if (!memo.MeasuredPoses.TryGetValue(poseKey, out var exact))
            {
                exact = CountMatches(source, query, queryCells, fittedScale, fittedTranslation);
                memo.MeasuredPoses.Add(poseKey, exact);
            }
            CheckGeometryBudget(cancellationToken);
            if (exact.Count >= 6)
            {
                var fit = new CornerFitPose(fittedScale, fittedTranslation.X, fittedTranslation.Y,
                    exact.Count, exact.Error);
                memo.Triplets[key] = fit;
                if (seen.Add((fit.Scale, fit.X, fit.Y))) result.Add(fit);
            }
        }
        return result;
    }

    private static string CornerCorrespondenceKey(
        IReadOnlyList<(int Source, int Query, double Error)> matches) =>
        string.Join(";", matches.Select(match => $"{match.Source}:{match.Query}"));

    private static IEnumerable<(List<(int Source, int Query, double Error)> Matches,
        CornerScaleInterval Interval)> EnumerateSixCornerMatches(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        IReadOnlyList<(int Source, int Query, double Error)> tentative,
        CancellationToken cancellationToken)
    {
        var cfg = Vpsg3TuningConfig.Default;
        var edges = new Dictionary<(int, int), CornerScaleInterval>();
        for (var ai = 0; ai < tentative.Count; ai++)
        for (var bi = ai + 1; bi < tentative.Count; bi++)
        {
            CheckGeometryBudget(cancellationToken);
            var a = tentative[ai];
            var b = tentative[bi];
            if (a.Source == b.Source || a.Query == b.Query
                || Distance(query[a.Query].Point, query[b.Query].Point) < 6) continue;
            var delta = source[b.Source].Point - source[a.Source].Point;
            var observedDelta = query[b.Query].Point - query[a.Query].Point;
            var denominator = Dot(delta, delta);
            if (denominator <= 0) continue;
            var center = Dot(observedDelta, delta) / denominator;
            var perpendicular = observedDelta - delta * center;
            var allowance = 4 * CornerMatchTolerance * CornerMatchTolerance
                - Dot(perpendicular, perpendicular);
            if (allowance < 0) continue;
            var radius = Math.Sqrt(allowance / denominator);
            var minimum = Math.Max(Math.Max(cfg.MinSupportedScale, center - radius),
                6 / Math.Sqrt(denominator));
            var maximum = Math.Min(cfg.MaxSupportedScale, center + radius);
            if (minimum > maximum || Math.Sqrt(denominator) * maximum < 6) continue;
            edges.Add((ai, bi), new(minimum, maximum));
        }

        var selected = new List<int>(6);
        foreach (var result in Visit(Enumerable.Range(0, tentative.Count).ToArray(),
            new(cfg.MinSupportedScale, cfg.MaxSupportedScale))) yield return result;

        IEnumerable<(List<(int Source, int Query, double Error)>, CornerScaleInterval)> Visit(
            IReadOnlyList<int> candidates, CornerScaleInterval range)
        {
            CheckGeometryBudget(cancellationToken);
            if (selected.Count == 6)
            {
                yield return (selected.Select(index => tentative[index]).ToList(), range);
                yield break;
            }
            for (var position = 0; position < candidates.Count; position++)
            {
                CheckGeometryBudget(cancellationToken);
                if (selected.Count + candidates.Count - position < 6) yield break;
                var index = candidates[position];
                var nextRange = range;
                var compatible = true;
                foreach (var prior in selected)
                {
                    if (!edges.TryGetValue((prior, index), out var edge))
                    {
                        compatible = false;
                        break;
                    }
                    nextRange = new(Math.Max(nextRange.Minimum, edge.Minimum),
                        Math.Min(nextRange.Maximum, edge.Maximum));
                    if (nextRange.Minimum > nextRange.Maximum)
                    {
                        compatible = false;
                        break;
                    }
                }
                if (!compatible) continue;
                var remaining = candidates.Skip(position + 1)
                    .Where(next => edges.ContainsKey((index, next))).ToArray();
                selected.Add(index);
                foreach (var result in Visit(remaining, nextRange)) yield return result;
                selected.RemoveAt(selected.Count - 1);
            }
        }
    }

    private static IReadOnlyList<CornerFitPose> FitSixCornerMatches(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        IReadOnlyList<(int Source, int Query, double Error)> matches,
        CornerScaleInterval range, CancellationToken cancellationToken, out bool uncertified) =>
        FitSixCornerMatchesCore(source, query, queryCells, matches, range,
            cancellationToken, out uncertified, null);

    private static IReadOnlyList<CornerFitPose> FitSixCornerMatchesCore(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        IReadOnlyList<(int Source, int Query, double Error)> matches,
        CornerScaleInterval range, CancellationToken cancellationToken, out bool uncertified,
        SixCornerFitDiagnostics? diagnostics, SixCornerFitCountMemo? countMemo = null)
    {
        uncertified = false;
        var sourceCenter = new Point2d(matches.Average(m => source[m.Source].Point.X),
            matches.Average(m => source[m.Source].Point.Y));
        var queryCenter = new Point2d(matches.Average(m => query[m.Query].Point.X),
            matches.Average(m => query[m.Query].Point.Y));
        var sourcePoints = matches.Select(m => source[m.Source].Point - sourceCenter).ToArray();
        var queryPoints = matches.Select(m => query[m.Query].Point - queryCenter).ToArray();
        var centers = new Point2d[matches.Count];
        CornerCircleFit Evaluate(double scale)
        {
            CheckGeometryBudget(cancellationToken);
            // MEC consumes this call-local scratch synchronously and returns
            // only value types. Keep the original coordinate evaluation order.
            for (var i = 0; i < centers.Length; i++)
                centers[i] = queryPoints[i] - sourcePoints[i] * scale;
            var circle = diagnostics is null
                ? MinimumCornerEnclosingCircle(centers, cancellationToken)
                : diagnostics.MeasureCircle(centers, cancellationToken);
            return new(scale, circle.Center + queryCenter - sourceCenter * scale, circle.Radius);
        }

        // R(s) = min_t max_i ||q_i - s*p_i - t|| is convex. At fixed s,
        // the optimum translation is the center of six points' smallest circle.
        var left = range.Minimum;
        var right = range.Maximum;
        var golden = (Math.Sqrt(5) - 1) * .5;
        var a = Evaluate(right - golden * (right - left));
        var b = Evaluate(left + golden * (right - left));
        var best = a.Radius <= b.Radius ? a : b;
        void Consider(CornerCircleFit fit)
        {
            if (fit.Radius < best.Radius) best = fit;
        }
        Consider(Evaluate(left));
        Consider(Evaluate(right));
        for (var iteration = 0; iteration < 48; iteration++)
        {
            CheckGeometryBudget(cancellationToken);
            if (a.Radius <= b.Radius)
            {
                right = b.Scale;
                b = a;
                a = Evaluate(right - golden * (right - left));
                Consider(a);
            }
            else
            {
                left = a.Scale;
                a = b;
                b = Evaluate(left + golden * (right - left));
                Consider(b);
            }
        }
        if (!double.IsFinite(best.Radius))
        {
            uncertified = true;
            return [];
        }
        if (best.Radius > CornerMatchTolerance)
        {
            // R is Lipschitz with constant max_i ||p_i - mean(p)||. A
            // numerically unresolved threshold contact must not become a
            // completed negative just because the bounded search stopped.
            var lipschitz = sourcePoints.Max(point => Distance(point, new Point2d(0, 0)));
            var lowerBound = Math.Min(a.Radius, b.Radius) - lipschitz * (right - left);
            uncertified = !double.IsFinite(lowerBound) || lowerBound <= CornerMatchTolerance;
            return [];
        }

        // Preserve both ends of the feasible scale family, not only its
        // minimum-radius representative. Bisection always returns the measured
        // feasible side; no epsilon expands the final three-pixel acceptance.
        List<CornerCircleFit> FindFeasibleEnd(double outsideScale)
        {
            var edge = Evaluate(outsideScale);
            if (edge.Radius <= CornerMatchTolerance) return [best, edge];
            var feasible = new List<CornerCircleFit> { best };
            var inside = best;
            var outside = outsideScale;
            for (var iteration = 0; iteration < 48; iteration++)
            {
                CheckGeometryBudget(cancellationToken);
                var middle = outside + (inside.Scale - outside) * .5;
                if (middle == outside || middle == inside.Scale) break;
                var fit = Evaluate(middle);
                if (fit.Radius <= CornerMatchTolerance)
                {
                    inside = fit;
                    feasible.Add(fit);
                }
                else outside = middle;
            }
            return feasible;
        }

        var result = new List<CornerFitPose>();
        bool AddVerified(CornerCircleFit fit)
        {
            CheckGeometryBudget(cancellationToken);
            // Recheck actual coordinates, directions and physical independence
            // after undoing centering and after every numerical fit.
            var exact = countMemo is null
                ? diagnostics is null
                    ? CountMatches(source, query, queryCells, fit.Scale, fit.Translation)
                    : diagnostics.MeasureVerification(source, query, queryCells, fit.Scale, fit.Translation)
                : countMemo.Resolve(fit.Scale, fit.Translation);
            if (exact.Count < 6) return false;
            result.Add(new(fit.Scale, fit.Translation.X, fit.Translation.Y,
                exact.Count, exact.Error));
            return true;
        }
        AddVerified(best);
        foreach (var end in new[] { range.Minimum, range.Maximum })
        {
            var feasible = FindFeasibleEnd(end);
            // Undoing centering can move a boundary residual by rounding.
            // Keep the closest actually verified interior sample, never add
            // an acceptance epsilon or silently lose both scale endpoints.
            for (var index = feasible.Count - 1; index >= 0; index--)
                if (AddVerified(feasible[index])) break;
        }
        if (result.Count == 0) uncertified = true;
        return result;
    }

    private static (Point2d Center, double Radius) MinimumCornerEnclosingCircle(
        IReadOnlyList<Point2d> points, CancellationToken cancellationToken)
    {
        if (points is Point2d[] array && array.Length != 0)
            return MinimumCornerEnclosingCircleArray(array, cancellationToken);
        var bestCenter = points[0];
        var bestSquared = double.PositiveInfinity;
        var pointCount = points.Count;
        void Consider(Point2d center)
        {
            CheckGeometryBudget(cancellationToken);
            if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)) return;
            var maximum = 0d;
            for (var index = 0; index < pointCount; index++)
            {
                var delta = points[index] - center;
                maximum = Math.Max(maximum, Dot(delta, delta));
                // Later points cannot reduce the maximum, and ties never
                // replace the existing circle. Keep the candidate order intact.
                if (maximum >= bestSquared) return;
            }
            if (maximum < bestSquared)
            {
                bestSquared = maximum;
                bestCenter = center;
            }
        }
        for (var i = 0; i < pointCount; i++)
        {
            Consider(points[i]);
            for (var j = i + 1; j < pointCount; j++)
            {
                Consider((points[i] + points[j]) * .5);
                for (var k = j + 1; k < pointCount; k++)
                {
                    var u = points[j] - points[i];
                    var v = points[k] - points[i];
                    var determinant = 2 * (u.X * v.Y - u.Y * v.X);
                    if (determinant == 0) continue;
                    var uu = Dot(u, u);
                    var vv = Dot(v, v);
                    Consider(points[i] + new Point2d(
                        (v.Y * uu - u.Y * vv) / determinant,
                        (u.X * vv - v.X * uu) / determinant));
                }
            }
        }
        return (bestCenter, Math.Sqrt(bestSquared));
    }

    private static List<(int Source, int Query, double Error)> FindCornerMatches(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        double scale, Point2d translation, Func<Point2d, double> toleranceForSource,
        CancellationToken cancellationToken, bool independentOnly = true)
    {
        var pairs = new List<(int Source, int Query, double Error)>();
        for (var si = 0; si < source.Count; si++)
        {
            CheckGeometryBudget(cancellationToken);
            var projected = source[si].Point * scale + translation;
            var tolerance = toleranceForSource(source[si].Point);
            var first = CornerCell(projected - new Point2d(tolerance, tolerance));
            var last = CornerCell(projected + new Point2d(tolerance, tolerance));
            // No query cell exists outside the observed image's occupied box.
            // Iterating occupied cells handles large uncertainty without a
            // quadratic walk through empty off-screen coordinates.
            void AddCell(int[] indices)
            {
                foreach (var qi in indices)
                {
                    var error = Distance(projected, query[qi].Point);
                    if (error <= tolerance && SameDirections(source[si], query[qi]))
                        pairs.Add((si, qi, error));
                }
            }
            var cellCount = (long)(last.X - first.X + 1) * (last.Y - first.Y + 1);
            if (cellCount <= queryCells.Count)
            {
                for (var cy = first.Y; cy <= last.Y; cy++)
                for (var cx = first.X; cx <= last.X; cx++)
                    if (queryCells.TryGetValue((cx, cy), out var indices)) AddCell(indices);
            }
            else
            {
                foreach (var cell in queryCells)
                    if (cell.Key.X >= first.X && cell.Key.X <= last.X
                        && cell.Key.Y >= first.Y && cell.Key.Y <= last.Y) AddCell(cell.Value);
            }
        }
        // The uncertainty band contains alternatives, not a selected matching.
        // Keep every correspondence for proposal generation so a nearer wrong
        // raster contour cannot hide a different pose or runner-up.
        if (!independentOnly) return pairs;
        var accepted = new List<(int Source, int Query, double Error)>();
        var usedSource = new HashSet<int>();
        var usedQuery = new HashSet<int>();
        foreach (var pair in pairs.OrderBy(pair => pair.Error))
        {
            if (usedSource.Contains(pair.Source) || usedQuery.Contains(pair.Query)) continue;
            if (usedQuery.Any(i => Distance(query[i].Point, query[pair.Query].Point) < 6)
                || usedSource.Any(i => Distance(source[i].Point, source[pair.Source].Point) * scale < 6))
                continue;
            usedSource.Add(pair.Source);
            usedQuery.Add(pair.Query);
            accepted.Add(pair);
        }
        return accepted;
    }
}
