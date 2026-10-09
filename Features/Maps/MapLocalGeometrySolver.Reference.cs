using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    private sealed record ReferenceAnchorPairs(Point2d Center, double Radius,
        IReadOnlyList<MapFrontEntryReferenceCornerPair> Pairs);

    private static IEnumerable<ReferenceAnchorPairs> GetReferenceAnchorPairs(
        Vpsg3PreparedFloor floor, IReadOnlyList<MapLocalCornerGeometry> source,
        IReadOnlyList<NormalizedRectangle> anchors)
    {
        var index = floor.AutomaticEntryIndex;
        foreach (var anchor in anchors)
        {
            var bounds = new MapFrontEntryReferenceBounds(
                anchor.X, anchor.Y, anchor.Width, anchor.Height);
            var cached = index?.CacheKey == floor.CacheKey
                ? index.Anchors.FirstOrDefault(candidate =>
                    candidate.NormalizedBounds == bounds && candidate.HasUsableBounds)
                : null;
            if (cached is not null && ReferenceEquals(source, index!.CornerGeometry))
            {
                yield return new(cached.Center!.Value, cached.Radius!.Value, cached.Pairs);
                continue;
            }

            // A cold index or changed anchor retains the original full search.
            var center = new Point2d((anchor.X + anchor.Width / 2) * floor.ReferenceWidth,
                (anchor.Y + anchor.Height / 2) * floor.ReferenceHeight);
            var radius = Math.Max(80, Math.Max(anchor.Width * floor.ReferenceWidth,
                anchor.Height * floor.ReferenceHeight) * 3);
            var near = Enumerable.Range(0, source.Count)
                .Where(i => Distance(source[i].Point, center) <= radius).ToArray();
            var pairs = new List<MapFrontEntryReferenceCornerPair>();
            for (var a = 0; a < near.Length; a++)
            for (var b = a + 1; b < near.Length; b++)
            {
                var delta = source[near[b]].Point - source[near[a]].Point;
                pairs.Add(new(near[a], near[b], delta, Dot(delta, delta),
                    (source[near[a]].Point + source[near[b]].Point) * .5));
            }
            yield return new(center, radius, pairs);
        }
    }

    private static IReadOnlyList<MapLocalCornerGeometry> GetPreparedReferenceCorners(
        Vpsg3PreparedFloor floor)
    {
        if (floor.AutomaticEntryIndex is { } entry && entry.CacheKey == floor.CacheKey)
            return entry.CornerGeometry;
        using var reference = new Mat(floor.ReferenceHeight, floor.ReferenceWidth,
            MatType.CV_8UC1, Scalar.Black);
        foreach (var point in floor.ReferenceEdgePoints.Span)
            reference.Set(point.Y, point.X, (byte)255);
        return MapLocalCornerGeometryExtractor.ExtractCorners(reference).ToArray();
    }
}
