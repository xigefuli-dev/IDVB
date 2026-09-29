using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapEntryIdentityFrame
{
    public (IReadOnlyList<MapEntryCornerNode> Nodes, IReadOnlyList<MapEntryCornerPair> Pairs) Corners()
    {
        Cv2.FindContours(Query, out Point[][] contours, out _, RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);
        using var supported = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(Observed, supported, kernel);
        var nodes = new List<MapEntryCornerNode>();
        var pairs = new List<MapEntryCornerPair>();
        foreach (var contour in contours)
        {
            var polygon = Cv2.ApproxPolyDP(contour, 2, true);
            if (polygon.Length < 4 || Math.Abs(Cv2.ContourArea(polygon)) < 100) continue;
            var indices = Enumerable.Repeat(-1, polygon.Length).ToArray();
            for (var i = 0; i < polygon.Length; i++)
            {
                var p = polygon[i];
                var previous = polygon[(i + polygon.Length - 1) % polygon.Length];
                var next = polygon[(i + 1) % polygon.Length];
                var l0 = Math.Sqrt(Math.Pow(previous.X - p.X, 2) + Math.Pow(previous.Y - p.Y, 2));
                var l1 = Math.Sqrt(Math.Pow(next.X - p.X, 2) + Math.Pow(next.Y - p.Y, 2));
                if (Math.Min(l0, l1) < 8) continue;
                var r0 = new MapEntryIdentityPoint((previous.X - p.X) / l0, (previous.Y - p.Y) / l0);
                var r1 = new MapEntryIdentityPoint((next.X - p.X) / l1, (next.Y - p.Y) / l1);
                if (Math.Abs(r0.X * r1.Y - r0.Y * r1.X) <= .45) continue;
                bool Supported(MapEntryIdentityPoint ray, double length)
                {
                    for (var step = 2; step < Math.Min(12, (int)length); step++)
                    {
                        var x = (int)Math.Round(p.X + step * ray.X);
                        var y = (int)Math.Round(p.Y + step * ray.Y);
                        if (x < 0 || y < 0 || x >= supported.Width || y >= supported.Height
                            || supported.At<byte>(y, x) == 0) return false;
                    }
                    return true;
                }
                if (!Supported(r0, l0) || !Supported(r1, l1)) continue;
                indices[i] = nodes.Count;
                nodes.Add(new(new(p.X, p.Y), r0, r1));
            }
            for (var i = 0; i < indices.Length; i++)
            {
                var next = (i + 1) % indices.Length;
                if (indices[i] >= 0 && indices[next] >= 0) pairs.Add(new(indices[i], indices[next]));
            }
        }
        return (nodes, pairs);
    }
}
