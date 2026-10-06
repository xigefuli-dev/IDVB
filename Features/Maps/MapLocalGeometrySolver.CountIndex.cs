using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using MapLocalCorner = IDVBuff.Features.Maps.MapLocalCornerGeometry;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    private const double CountMatchIndexCoordinateLimit = 1_048_576d;
    private const double CountMatchIndexMinimumScale = .4d;
    private const double CountMatchIndexMaximumScale = 2.4d;
    private const double CountMatchIndexSourceCellSize = 32d;

    private sealed class CountMatchQueryCells(
        IReadOnlyList<MapLocalCorner> sourceOwner,
        IReadOnlyList<MapLocalCorner> queryOwner)
        : Dictionary<(int X, int Y), int[]>
    {
        internal IReadOnlyList<MapLocalCorner> SourceOwner { get; } = sourceOwner;
        internal IReadOnlyList<MapLocalCorner> QueryOwner { get; } = queryOwner;
        internal CountMatchIndex? Index { get; private set; }

        internal void SetIndex(CountMatchIndex? index) => Index = index;
    }

    private sealed class CountMatchIndex
    {
        private readonly IReadOnlyList<MapLocalCorner> sourceOwner;
        private readonly IReadOnlyList<MapLocalCorner> queryOwner;
        private readonly Point2d[] sourcePoints;
        private readonly Point2d[] queryPoints;
        private readonly Dictionary<(int X, int Y), int[]> sourceCells;
        private readonly QueryCell[] queryCells;
        private readonly bool[][] queryNeighbors;
        private readonly bool ownsQuerySnapshot;

        private CountMatchIndex(IReadOnlyList<MapLocalCorner> sourceOwner,
            IReadOnlyList<MapLocalCorner> queryOwner,
            Point2d[] sourcePoints, Point2d[] queryPoints,
            Dictionary<(int X, int Y), int[]> sourceCells, QueryCell[] queryCells,
            bool[][] queryNeighbors, bool ownsQuerySnapshot)
        {
            this.sourceOwner = sourceOwner;
            this.queryOwner = queryOwner;
            this.sourcePoints = sourcePoints;
            this.queryPoints = queryPoints;
            this.sourceCells = sourceCells;
            this.queryCells = queryCells;
            this.queryNeighbors = queryNeighbors;
            this.ownsQuerySnapshot = ownsQuerySnapshot;
        }

        internal static CountMatchIndex? TryCreate(
            IReadOnlyList<MapLocalCorner> sourceOwner,
            IReadOnlyList<MapLocalCorner> queryOwner,
            CountMatchQueryCells cells, CancellationToken cancellationToken, bool ownsQuerySnapshot)
        {
            if (!ReferenceEquals(cells.Comparer, EqualityComparer<(int X, int Y)>.Default))
                return null;

            var sourcePoints = sourceOwner.Select(corner => corner.Point).ToArray();
            var queryPoints = queryOwner.Select(corner => corner.Point).ToArray();
            if (sourcePoints.Any(point => !SupportedPoint(point))
                || queryPoints.Any(point => !SupportedPoint(point)))
                return null;

            var rows = new List<QueryCell>(cells.Count);
            foreach (var entry in cells)
            {
                if (entry.Value is null) return null;
                var indices = (int[])entry.Value.Clone();
                foreach (var queryIndex in indices)
                    if ((uint)queryIndex >= (uint)queryPoints.Length
                        || CornerCell(queryPoints[queryIndex]) != entry.Key)
                        return null;
                if (indices.Length != 0)
                    rows.Add(new(entry.Key.X, entry.Key.Y, indices));
            }

            var buckets = new Dictionary<(int X, int Y), List<int>>();
            for (var sourceIndex = 0; sourceIndex < sourcePoints.Length; sourceIndex++)
            {
                var cell = SourceCell(sourcePoints[sourceIndex]);
                if (!buckets.TryGetValue(cell, out var indices))
                    buckets.Add(cell, indices = []);
                indices.Add(sourceIndex);
            }

            var sourceCells = buckets.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
            var queryCells = rows.ToArray();
            var queryNeighbors = new bool[queryPoints.Length][];
            for (var i = 0; i < queryPoints.Length; i++)
            {
                CheckGeometryBudget(cancellationToken);
                queryNeighbors[i] = new bool[queryPoints.Length];
                for (var j = 0; j < queryPoints.Length; j++)
                    queryNeighbors[i][j] = Distance(queryPoints[i], queryPoints[j]) < 6;
            }
            CheckGeometryBudget(cancellationToken);
            return new(sourceOwner, queryOwner, sourcePoints, queryPoints,
                sourceCells, queryCells, queryNeighbors, ownsQuerySnapshot);
        }

        internal bool[][]? TryGetNeighbors(IReadOnlyList<MapLocalCorner> currentQuery)
        {
            if (!ReferenceEquals(queryOwner, currentQuery)
                || currentQuery.Count != queryPoints.Length)
                return null;
            if (ownsQuerySnapshot) return queryNeighbors;
            // The two-argument factory accepts caller-owned arrays. Retain
            // scalar behavior if that caller changes points in the same owner.
            // Ordinary equality permits harmless signed-zero changes.
            for (var i = 0; i < queryPoints.Length; i++)
            {
                var point = currentQuery[i].Point;
                if (point.X != queryPoints[i].X || point.Y != queryPoints[i].Y)
                    return null;
            }
            return queryNeighbors;
        }

        internal List<(int Source, int Query, double Error)>? TryCollect(
            IReadOnlyList<MapLocalCorner> currentSource,
            IReadOnlyList<MapLocalCorner> currentQuery,
            double scale, Point2d translation)
        {
            if (!ReferenceEquals(sourceOwner, currentSource)
                || !ReferenceEquals(queryOwner, currentQuery)
                || !double.IsFinite(scale)
                || scale < CountMatchIndexMinimumScale
                || scale > CountMatchIndexMaximumScale
                || !SupportedPoint(translation))
                return null;

            var pairs = new List<CountMatchPair>();
            foreach (var row in queryCells)
            {
                if (!TryAxisCells(row.X, translation.X, scale, out var lowX, out var highX)
                    || !TryAxisCells(row.Y, translation.Y, scale, out var lowY, out var highY))
                    return null;

                for (var cellY = lowY; cellY <= highY; cellY++)
                for (var cellX = lowX; cellX <= highX; cellX++)
                {
                    if (!sourceCells.TryGetValue((cellX, cellY), out var indices)) continue;
                    foreach (var sourceIndex in indices)
                    {
                        // Keep the original forward projection and cell test;
                        // inverse arithmetic only chooses a conservative bucket set.
                        var projected = sourcePoints[sourceIndex] * scale + translation;
                        var projectedCell = CornerCell(projected);
                        if (row.Y < projectedCell.Y - 1 || row.Y > projectedCell.Y + 1
                            || row.X < projectedCell.X - 1 || row.X > projectedCell.X + 1)
                            continue;

                        for (var ordinal = 0; ordinal < row.Indices.Length; ordinal++)
                        {
                            var queryIndex = row.Indices[ordinal];
                            var error = Distance(projected, queryPoints[queryIndex]);
                            if (error <= CornerMatchTolerance
                                && SameDirections(sourceOwner[sourceIndex], queryOwner[queryIndex]))
                            {
                                pairs.Add(new(sourceIndex, queryIndex, error,
                                    row.X, row.Y, ordinal));
                            }
                        }
                    }
                }
            }

            // Rebuild the old source/cell/index traversal before the unchanged
            // stable OrderBy(Error) and greedy consumer.
            pairs.Sort(static (left, right) =>
            {
                var order = left.Source.CompareTo(right.Source);
                if (order != 0) return order;
                order = left.CellY.CompareTo(right.CellY);
                if (order != 0) return order;
                order = left.CellX.CompareTo(right.CellX);
                return order != 0 ? order : left.Ordinal.CompareTo(right.Ordinal);
            });

            var result = new List<(int Source, int Query, double Error)>(pairs.Count);
            foreach (var pair in pairs)
                result.Add((pair.Source, pair.Query, pair.Error));
            return result;
        }

        private static bool TryAxisCells(int queryCell, double translation, double scale,
            out int low, out int high)
        {
            // A legacy visited projected cell is in [queryCell-1, queryCell+1].
            // Reverse every rounded operation outward, including the original
            // projected-point division by eight and its signed-zero preimage.
            var projectedLow = Math.BitDecrement(Math.BitDecrement(queryCell - 1d) * 8d);
            var projectedHigh = Math.BitIncrement(Math.BitIncrement(queryCell + 2d) * 8d);
            var productLow = Math.BitDecrement(projectedLow - translation);
            var productHigh = Math.BitIncrement(projectedHigh - translation);
            var sourceLow = Math.BitDecrement(Math.BitDecrement(productLow) / scale);
            var sourceHigh = Math.BitIncrement(Math.BitIncrement(productHigh) / scale);
            var cellLow = Math.Floor(sourceLow / CountMatchIndexSourceCellSize);
            var cellHigh = Math.Floor(sourceHigh / CountMatchIndexSourceCellSize);
            low = high = 0;
            if (!double.IsFinite(cellLow) || !double.IsFinite(cellHigh)
                || cellLow < int.MinValue || cellHigh > int.MaxValue || cellLow > cellHigh)
                return false;
            low = (int)cellLow;
            high = (int)cellHigh;
            // The supported domain spans at most three buckets per axis. Fall
            // back rather than truncating if arithmetic ever violates that bound.
            return (long)high - low <= 2;
        }

        private static bool SupportedPoint(Point2d point) =>
            double.IsFinite(point.X) && double.IsFinite(point.Y)
            && Math.Abs(point.X) <= CountMatchIndexCoordinateLimit
            && Math.Abs(point.Y) <= CountMatchIndexCoordinateLimit;

        private static (int X, int Y) SourceCell(Point2d point) =>
            ((int)Math.Floor(point.X / CountMatchIndexSourceCellSize),
                (int)Math.Floor(point.Y / CountMatchIndexSourceCellSize));

        private readonly record struct QueryCell(int X, int Y, int[] Indices);
        private readonly record struct CountMatchPair(int Source, int Query, double Error,
            int CellX, int CellY, int Ordinal);
    }

    private static Dictionary<(int X, int Y), int[]> CreateCountMatchQueryCells(
        IReadOnlyList<MapLocalCorner> source,
        IReadOnlyList<MapLocalCorner> query) =>
        CreateCountMatchQueryCellsImplementation(source, query, CancellationToken.None, false);

    private static Dictionary<(int X, int Y), int[]> CreateCountMatchQueryCellsCore(
        IReadOnlyList<MapLocalCorner> source,
        IReadOnlyList<MapLocalCorner> query, CancellationToken cancellationToken) =>
        // SolveCoreWithDiagnostics owns the query.ToArray() snapshot and does
        // not write it during its single search; workers only read this index.
        CreateCountMatchQueryCellsImplementation(source, query, cancellationToken, true);

    private static Dictionary<(int X, int Y), int[]> CreateCountMatchQueryCellsImplementation(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        CancellationToken cancellationToken, bool ownsQuerySnapshot)
    {
        CheckGeometryBudget(cancellationToken);
        var mutableCells = new Dictionary<(int X, int Y), List<int>>();
        for (var queryIndex = 0; queryIndex < query.Count; queryIndex++)
        {
            var cell = CornerCell(query[queryIndex].Point);
            if (!mutableCells.TryGetValue(cell, out var indices))
                mutableCells.Add(cell, indices = []);
            indices.Add(queryIndex);
        }

        var cells = new CountMatchQueryCells(source, query);
        foreach (var entry in mutableCells)
            cells.Add(entry.Key, entry.Value.ToArray());
        cells.SetIndex(CountMatchIndex.TryCreate(source, query, cells, cancellationToken, ownsQuerySnapshot));
        return cells;
    }

    private static bool[][]? TryGetCountQueryNeighbors(
        IReadOnlyList<MapLocalCorner> source, IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells)
    {
        if (queryCells is not CountMatchQueryCells indexedCells
            || !ReferenceEquals(indexedCells.SourceOwner, source)
            || !ReferenceEquals(indexedCells.QueryOwner, query)
            || indexedCells.Index is not { } index)
            return null;
        return index.TryGetNeighbors(query);
    }

    private static List<(int Source, int Query, double Error)>? TryCollectCountPairs(
        IReadOnlyList<MapLocalCorner> source,
        IReadOnlyList<MapLocalCorner> query,
        Dictionary<(int X, int Y), int[]> queryCells,
        double scale, Point2d translation)
    {
        if (queryCells is not CountMatchQueryCells indexedCells
            || !ReferenceEquals(indexedCells.SourceOwner, source)
            || !ReferenceEquals(indexedCells.QueryOwner, query)
            || indexedCells.Index is not { } index)
            return null;
        return index.TryCollect(source, query, scale, translation);
    }
}
