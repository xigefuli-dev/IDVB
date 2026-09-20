using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapStructureScaleSearch
{
    internal static Rect FindTemplateBounds(Mat edges)
    {
        var allPoints = FindNonZeroPoints(edges);
        if (allPoints.Length == 0)
            return new Rect();
        var allBounds = Cv2.BoundingRect(allPoints);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            edges, labels, stats, centroids, PixelConnectivity.Connectivity8);
        if (count <= 2)
            return allBounds;

        var components = Enumerable.Range(1, count - 1)
            .Select(label => new
            {
                Area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area),
                Bounds = new Rect(
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Top),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Width),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Height))
            })
            .Where(component => component.Area >= 12)
            .ToArray();
        if (components.Length <= 1)
            return allBounds;

        var attachmentDistance = Math.Clamp(
            Math.Min(edges.Width, edges.Height) / 30,
            12,
            48);
        var visited = new bool[components.Length];
        var clusters = new List<(int Area, Rect Bounds)>();
        for (var start = 0; start < components.Length; start++)
        {
            if (visited[start])
                continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            var area = 0;
            var bounds = components[start].Bounds;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                area += components[current].Area;
                bounds = Union(bounds, components[current].Bounds);
                for (var candidate = 0; candidate < components.Length; candidate++)
                {
                    if (visited[candidate]
                        || RectangleDistance(
                            components[current].Bounds,
                            components[candidate].Bounds) > attachmentDistance)
                    {
                        continue;
                    }
                    visited[candidate] = true;
                    queue.Enqueue(candidate);
                }
            }
            clusters.Add((area, bounds));
        }

        var dominant = clusters.MaxBy(cluster => cluster.Area);
        var totalArea = components.Sum(component => component.Area);
        var dominantBoundsArea = dominant.Bounds.Width * dominant.Bounds.Height;
        var allBoundsArea = allBounds.Width * allBounds.Height;
        // Only discard detached edges when one compact cluster owns most of
        // the evidence. This preserves genuinely disconnected map geometry.
        return dominant.Area * 2 >= totalArea
            && dominantBoundsArea * 4 <= allBoundsArea
            ? dominant.Bounds
            : allBounds;
    }

    private static Rect Union(Rect first, Rect second)
    {
        var left = Math.Min(first.Left, second.Left);
        var top = Math.Min(first.Top, second.Top);
        var right = Math.Max(first.Right, second.Right);
        var bottom = Math.Max(first.Bottom, second.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static double RectangleDistance(Rect first, Rect second)
    {
        var horizontal = Math.Max(
            0,
            Math.Max(first.Left - second.Right, second.Left - first.Right));
        var vertical = Math.Max(
            0,
            Math.Max(first.Top - second.Bottom, second.Top - first.Bottom));
        return Math.Sqrt((horizontal * horizontal) + (vertical * vertical));
    }
}
