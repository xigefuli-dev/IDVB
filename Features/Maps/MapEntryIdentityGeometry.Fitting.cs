namespace IDVBuff.Features.Maps;

public static partial class MapEntryIdentityGeometry
{
    private sealed record WallFit(MapEntryIdentityPose Pose,
        IReadOnlyList<MapEntryIdentityPlane> Planes, MapEntryIdentityHeldOut HeldOut);
    private sealed record FitAttempt(WallFit? Fit, string Reason);
    private sealed class BudgetExpiredException : Exception { }

    private static void CheckBudget(Func<bool> canCompute)
    {
        if (!canCompute()) throw new BudgetExpiredException();
    }

    // Equivalent to _fit_entry_local_walls + initial_wall_geometry at 9491d0b0.
    // Only the compatible local query subset is fitted. Whole-frame/author validation belongs to the caller.
    private static FitAttempt FitLocalWalls(IReadOnlyList<MapEntryIdentityWall> query,
        IReadOnlyList<MapEntryIdentityWall> source, MapEntryIdentityPose seed,
        Dictionary<string, FitAttempt> cache, Func<bool> canCompute, bool checkAllObservedFragments = false)
    {
        var assignments = new List<(int Query, int Source)>();
        for (var qi = 0; qi < query.Count; qi++)
        {
            CheckBudget(canCompute);
            var bestDistance = double.PositiveInfinity;
            var bestSource = -1;
            var minimumNormal = double.PositiveInfinity;
            var maximumNormal = double.NegativeInfinity;
            for (var si = 0; si < source.Count; si++)
            {
                if ((si & 127) == 0) CheckBudget(canCompute);
                var distance = WallDistance(query[qi], source[si], seed);
                if (distance > 3) continue;
                minimumNormal = Math.Min(minimumNormal, source[si].Normal);
                maximumNormal = Math.Max(maximumNormal, source[si].Normal);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestSource = si;
                }
            }
            if (bestSource < 0) continue;
            if (maximumNormal - minimumNormal > 3)
                return new(null, "ambiguous-source-wall-normal");
            assignments.Add((qi, bestSource));
        }
        if (assignments.Count == 0) return new(null, "no-compatible-local-walls");

        // The ordered subset AND exact segment assignment determine this same-frame fit.
        var key = string.Join(";", assignments.Select(pair => $"{pair.Query}:{pair.Source}"));
        if (cache.TryGetValue(key, out var cached)) return cached;
        FitAttempt Reject(string reason) => cache[key] = new(null, reason);
        var planes = new List<MapEntryIdentityPlane>();
        foreach (var (qi, si) in assignments)
        {
            var q = query[qi];
            var s = source[si];
            if (planes.Any(g => g.Axis == q.Axis && g.Sign == q.Sign && Math.Abs(g.QueryNormal - q.Normal) <= 3))
                continue;
            if (planes.Any(g => g.Axis == q.Axis && g.Sign == q.Sign && Math.Abs(g.SourceNormal - s.Normal) <= 3))
                return Reject("source-wall-reused-for-independent-query-planes");
            planes.Add(new(q.Axis, q.Sign, q.Normal, s.Normal, q.EdgeId, s.EdgeId));
        }
        for (var axis = 0; axis < 2; axis++)
        {
            var normals = planes.Where(g => g.Axis == axis).Select(g => g.QueryNormal).ToArray();
            if (normals.Length < 2 || normals.Max() - normals.Min() < 24)
                return Reject("independent-wall-planes-or-span-insufficient");
        }
        if (!TrySolve(planes, -1, out var pose)) return Reject("wall-fit-rank-or-scale-invalid");

        var fragmentIndices = checkAllObservedFragments ? Enumerable.Range(0, query.Count) : assignments.Select(pair => pair.Query);
        foreach (var qi in fragmentIndices)
        {
            CheckBudget(canCompute);
            var supported = false;
            for (var si = 0; si < source.Count; si++)
            {
                if ((si & 127) == 0) CheckBudget(canCompute);
                if (WallDistance(query[qi], source[si], pose) > 6) continue;
                supported = true;
                break;
            }
            if (!supported) return Reject("local-observed-fragment-unexplained");
        }

        var errors = new double[planes.Count];
        for (var i = 0; i < planes.Count; i++)
        {
            CheckBudget(canCompute);
            if (!TrySolve(planes, i, out var heldOutPose)) return Reject("held-out-wall-fit-rank-or-scale-invalid");
            var plane = planes[i];
            errors[i] = Math.Abs(heldOutPose.Scale * plane.SourceNormal +
                (plane.Axis == 0 ? heldOutPose.Tx : heldOutPose.Ty) - plane.QueryNormal);
        }
        Array.Sort(errors);
        var heldOut = new MapEntryIdentityHeldOut(errors.Length,
            Percentile(errors, .5), Percentile(errors, .9), errors[^1]);
        if (heldOut.MedianPixels > 3 || heldOut.P90Pixels > 8 || heldOut.MaxPixels > 20)
            return Reject("held-out-wall-normal-error");
        return cache[key] = new(new(pose, planes.ToArray(), heldOut), "local-wall-fit");
    }

    /// <summary>
    /// Continuous directed-normal fit with leave-one-plane-out validation. Unlike passport fitting,
    /// every supplied observed fragment must be explained; no compatible-subset shortcut is used.
    /// The caller still owns raster/unknown-mask and actual-author verification.
    /// </summary>
    public static MapEntryIdentityWallFit RefineObservedWalls(IReadOnlyList<MapEntryIdentityWall> observedWalls,
        IReadOnlyList<MapEntryIdentityWall> sourceWalls, MapEntryIdentityPose seed, Func<bool> canCompute)
    {
        if (observedWalls.Any(w => !ValidWall(w)) || sourceWalls.Any(w => !ValidWall(w)) ||
            seed.Scale <= 0 || !double.IsFinite(seed.Scale) || !double.IsFinite(seed.Tx) || !double.IsFinite(seed.Ty))
            return new(false, seed, [], null, "invalid-wall-refinement-input");
        try
        {
            CheckBudget(canCompute);
            var result = FitLocalWalls(observedWalls, sourceWalls, seed, new(StringComparer.Ordinal), canCompute, true);
            return result.Fit is { } fit ? new(true, fit.Pose, fit.Planes, fit.HeldOut, "all-observed-wall-normal-fit") :
                new(false, seed, [], null, result.Reason);
        }
        catch (BudgetExpiredException) { return new(false, seed, [], null, "entry-budget-incomplete"); }
    }

    private static double WallDistance(MapEntryIdentityWall query, MapEntryIdentityWall source,
        MapEntryIdentityPose pose)
    {
        if (query.Axis != source.Axis || query.Sign != source.Sign) return double.PositiveInfinity;
        var tangentShift = query.Axis == 0 ? pose.Ty : pose.Tx;
        var overlap = Math.Min(query.Hi, pose.Scale * source.Hi + tangentShift) -
            Math.Max(query.Lo, pose.Scale * source.Lo + tangentShift);
        if (overlap < Math.Min(12, query.Hi - query.Lo - 1)) return double.PositiveInfinity;
        return Math.Abs(pose.Scale * source.Normal + (query.Axis == 0 ? pose.Tx : pose.Ty) - query.Normal);
    }

    // Center separately on each axis: algebraically the rank-3 least-squares fit [s, tx, ty].
    // Tangent endpoints never enter this solve; fog-clipped endpoints are not measured corners.
    private static bool TrySolve(IReadOnlyList<MapEntryIdentityPlane> planes, int excluded,
        out MapEntryIdentityPose pose)
    {
        pose = default;
        var count = new int[2];
        var sourceMean = new double[2];
        var queryMean = new double[2];
        for (var i = 0; i < planes.Count; i++)
        {
            if (i == excluded) continue;
            var p = planes[i];
            count[p.Axis]++;
            sourceMean[p.Axis] += p.SourceNormal;
            queryMean[p.Axis] += p.QueryNormal;
        }
        if (count[0] == 0 || count[1] == 0) return false;
        for (var axis = 0; axis < 2; axis++)
        {
            sourceMean[axis] /= count[axis];
            queryMean[axis] /= count[axis];
        }
        double numerator = 0, denominator = 0;
        for (var i = 0; i < planes.Count; i++)
        {
            if (i == excluded) continue;
            var p = planes[i];
            var sourceDelta = p.SourceNormal - sourceMean[p.Axis];
            numerator += sourceDelta * (p.QueryNormal - queryMean[p.Axis]);
            denominator += sourceDelta * sourceDelta;
        }
        if (denominator <= 1e-12) return false;
        var scale = numerator / denominator;
        pose = new(scale, queryMean[0] - scale * sourceMean[0], queryMean[1] - scale * sourceMean[1]);
        return scale > 0 && double.IsFinite(scale) && double.IsFinite(pose.Tx) && double.IsFinite(pose.Ty);
    }

    // Shared exact normal-equation solve; callers own their independently chosen support/holdout sets.
    internal static bool TryFitPlaneCorrespondences(IReadOnlyList<MapEntryIdentityPlane> planes,
        out MapEntryIdentityPose model) => TrySolve(planes, -1, out model);

    private static double Percentile(double[] sorted, double fraction)
    {
        var position = (sorted.Length - 1) * fraction;
        var lo = (int)Math.Floor(position);
        var hi = (int)Math.Ceiling(position);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (position - lo);
    }

    private static bool CorrespondenceExtends(WallFit previous, WallFit proposed) =>
        previous.Planes.All(g => !string.IsNullOrEmpty(g.QueryEdge) && !string.IsNullOrEmpty(g.SourceEdge) &&
            proposed.Planes.Any(h => g.Axis == h.Axis && g.Sign == h.Sign &&
                g.QueryEdge == h.QueryEdge && g.SourceEdge == h.SourceEdge));
}
