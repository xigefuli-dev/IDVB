namespace IDVBuff.Features.Maps;

public static partial class MapEntryCornerIdentity
{
    private sealed class BudgetExpiredException : Exception { }
    private static void CheckBudget(Func<bool> canCompute)
    {
        if (!canCompute()) throw new BudgetExpiredException();
    }
    private static MapEntryIdentityPoint Subtract(MapEntryIdentityPoint a, MapEntryIdentityPoint b) => new(a.X - b.X, a.Y - b.Y);
    private static double Dot(MapEntryIdentityPoint a, MapEntryIdentityPoint b) => a.X * b.X + a.Y * b.Y;
    private static double Distance(MapEntryIdentityPoint a, MapEntryIdentityPoint b) => Math.Sqrt(Dot(Subtract(a, b), Subtract(a, b)));
    private static bool Finite(MapEntryIdentityPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static bool ValidPose(MapEntryIdentityPose p) => p.Scale > 0 && double.IsFinite(p.Scale) && double.IsFinite(p.Tx) && double.IsFinite(p.Ty);
    private static bool Oriented(MapEntryCornerNode q, MapEntryCornerNode s) => Dot(q.Ray0, s.Ray0) >= .95 && Dot(q.Ray1, s.Ray1) >= .95;
    private static bool ValidNode(MapEntryCornerNode n) => Finite(n.Point) && Finite(n.Ray0) && Finite(n.Ray1);
    private static bool ValidRegion(MapEntryCornerRegion r) => r.Unit > 0 && double.IsFinite(r.Unit) && Finite(r.CenterSource) &&
        r.Nodes.All(ValidNode) && r.Pairs.All(p => p.From >= 0 && p.From < r.Nodes.Count && p.To >= 0 && p.To < r.Nodes.Count) &&
        r.GateCenters.All(Finite);

    private static List<MapEntryCornerMatch> OrientedPairs(IReadOnlyList<MapEntryCornerNode> query,
        IReadOnlyList<MapEntryCornerNode> source, Func<bool> canCompute)
    {
        var result = new List<MapEntryCornerMatch>();
        for (var qi = 0; qi < query.Count; qi++)
        {
            CheckBudget(canCompute);
            for (var si = 0; si < source.Count; si++)
                if (Oriented(query[qi], source[si])) result.Add(new(qi, si));
        }
        return result;
    }

    // Candidate-independent query buckets replace repeated visits to distant
    // oriented pairs. Four-pixel cells exceed the unchanged three-pixel match
    // radius, including its floating-point boundary. Match order remains the
    // original query-major/source-major order before any fit or uniqueness test.
    private sealed class CornerMatcher
    {
        private readonly IReadOnlyList<MapEntryCornerNode> _query;
        private readonly IReadOnlyList<MapEntryCornerNode> _source;
        private readonly IReadOnlyList<MapEntryCornerMatch> _oriented;
        private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
        private readonly bool[,] _compatible;
        private readonly int[] _queryCounts;
        private readonly int[] _sourceCounts;
        private readonly List<MapEntryCornerMatch> _hits = [];
        private readonly bool _indexed;

        public CornerMatcher(IReadOnlyList<MapEntryCornerNode> query, IReadOnlyList<MapEntryCornerNode> source,
            IReadOnlyList<MapEntryCornerMatch> oriented)
        {
            _query = query; _source = source; _oriented = oriented;
            _compatible = new bool[query.Count, source.Count];
            _queryCounts = new int[query.Count]; _sourceCounts = new int[source.Count];
            foreach (var pair in oriented) _compatible[pair.QueryIndex, pair.SourceIndex] = true;
            for (var qi = 0; qi < query.Count; qi++)
            {
                // Unusual finite coordinates retain the original exhaustive
                // calculation instead of overflowing the spatial key.
                if (!TryCell(query[qi].Point, 2, out var key)) return;
                if (!_cells.TryGetValue(key, out var members)) _cells[key] = members = [];
                members.Add(qi);
            }
            _indexed = true;
        }

        public IReadOnlyList<MapEntryCornerMatch> Match(MapEntryIdentityPose pose, Func<bool> canCompute)
        {
            if (!_indexed) return UniqueMatches(_query, _source, _oriented, pose, canCompute);
            Array.Clear(_queryCounts); Array.Clear(_sourceCounts); _hits.Clear();
            for (var si = 0; si < _source.Count; si++)
            {
                if ((si & 31) == 0) CheckBudget(canCompute);
                var projected = pose.Apply(_source[si].Point);
                if (!TryCell(projected, 1, out var cell)) continue;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!_cells.TryGetValue((cell.X + dx, cell.Y + dy), out var members)) continue;
                    foreach (var qi in members)
                    {
                        if (!_compatible[qi, si] || Distance(_query[qi].Point, projected) > 3) continue;
                        _queryCounts[qi]++; _sourceCounts[si]++;
                        _hits.Add(new(qi, si));
                    }
                }
            }
            _hits.Sort(static (a, b) => a.QueryIndex != b.QueryIndex
                ? a.QueryIndex.CompareTo(b.QueryIndex) : a.SourceIndex.CompareTo(b.SourceIndex));
            return _hits.Where(p => _queryCounts[p.QueryIndex] == 1 && _sourceCounts[p.SourceIndex] == 1).ToArray();
        }

        private static bool TryCell(MapEntryIdentityPoint point, int margin, out (int X, int Y) cell)
        {
            var x = Math.Floor(point.X / 4); var y = Math.Floor(point.Y / 4);
            cell = default;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < int.MinValue + margin || x > int.MaxValue - margin
                || y < int.MinValue + margin || y > int.MaxValue - margin) return false;
            cell = ((int)x, (int)y);
            return true;
        }
    }

    private static IReadOnlyList<MapEntryCornerMatch> UniqueMatches(IReadOnlyList<MapEntryCornerNode> query,
        IReadOnlyList<MapEntryCornerNode> source, IReadOnlyList<MapEntryCornerMatch> oriented,
        MapEntryIdentityPose pose, Func<bool> canCompute)
    {
        var queryCounts = new int[query.Count];
        var sourceCounts = new int[source.Count];
        var hits = new List<MapEntryCornerMatch>();
        for (var i = 0; i < oriented.Count; i++)
        {
            if ((i & 255) == 0) CheckBudget(canCompute);
            var pair = oriented[i];
            if (Distance(query[pair.QueryIndex].Point, pose.Apply(source[pair.SourceIndex].Point)) > 3) continue;
            queryCounts[pair.QueryIndex]++;
            sourceCounts[pair.SourceIndex]++;
            hits.Add(pair);
        }
        return hits.Where(p => queryCounts[p.QueryIndex] == 1 && sourceCounts[p.SourceIndex] == 1).ToArray();
    }

    private static bool FitPoints(IReadOnlyList<MapEntryCornerNode> query, IReadOnlyList<MapEntryCornerNode> source,
        IReadOnlyList<MapEntryCornerMatch> matches, out MapEntryIdentityPose pose)
    {
        pose = default;
        if (matches.Count < 2) return false;
        double sx = 0, sy = 0, qx = 0, qy = 0;
        foreach (var pair in matches)
        {
            sx += source[pair.SourceIndex].Point.X;
            sy += source[pair.SourceIndex].Point.Y;
            qx += query[pair.QueryIndex].Point.X;
            qy += query[pair.QueryIndex].Point.Y;
        }
        sx /= matches.Count; sy /= matches.Count; qx /= matches.Count; qy /= matches.Count;
        double numerator = 0, denominator = 0;
        foreach (var pair in matches)
        {
            var s = source[pair.SourceIndex].Point;
            var q = query[pair.QueryIndex].Point;
            numerator += (s.X - sx) * (q.X - qx) + (s.Y - sy) * (q.Y - qy);
            denominator += (s.X - sx) * (s.X - sx) + (s.Y - sy) * (s.Y - sy);
        }
        if (denominator <= 1e-12) return false;
        var scale = numerator / denominator;
        pose = new(scale, qx - scale * sx, qy - scale * sy);
        return ValidPose(pose);
    }

    private static MapEntryIdentityPoint Extent(MapEntryCornerRegion region, IEnumerable<MapEntryCornerMatch> pairs)
    {
        var points = pairs.Select(p => region.Nodes[p.SourceIndex].Point).ToArray();
        return points.Length == 0 ? default : new((points.Max(p => p.X) - points.Min(p => p.X)) / region.Unit,
            (points.Max(p => p.Y) - points.Min(p => p.Y)) / region.Unit);
    }

    private static bool TryPairPose(MapEntryIdentityPoint sourceA, MapEntryIdentityPoint sourceB,
        MapEntryIdentityPoint queryA, MapEntryIdentityPoint queryB, double unit, double minimumSourceGrid,
        double maximumError, double maximumCellSize, out MapEntryIdentityPose pose)
    {
        pose = default;
        var delta = Subtract(sourceB, sourceA);
        var denominator = Dot(delta, delta);
        if (denominator < minimumSourceGrid * minimumSourceGrid * unit * unit) return false;
        var queryDelta = Subtract(queryB, queryA);
        var scale = Dot(queryDelta, delta) / denominator;
        if (scale * unit < 8 || scale * unit > maximumCellSize) return false;
        if (Distance(queryDelta, new(scale * delta.X, scale * delta.Y)) > maximumError) return false;
        pose = new(scale, queryA.X - scale * sourceA.X, queryA.Y - scale * sourceA.Y);
        return ValidPose(pose);
    }

    private static double MaximumDisplacement(MapEntryCornerRegion region, MapEntryIdentityPose a, MapEntryIdentityPose b) =>
        region.Nodes.Max(node => Distance(a.Apply(node.Point), b.Apply(node.Point)));

    private static MapEntryCornerIdentitySearch Empty(string reason, bool complete = true) => new([], complete, false, 0, true, reason);
}
