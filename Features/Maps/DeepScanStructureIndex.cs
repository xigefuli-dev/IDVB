using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Read-only local contour geometry, owned by one catalog floor revision.
/// Neither construction nor querying changes the shared contour/distance index.</summary>
internal sealed class DeepScanStructureIndex
{
    private static readonly ConditionalWeakTable<Mat, DeepScanStructureIndex> Cache = new();
    private const double AngleStep = Math.PI / 36;
    private const double RatioStep = .12;
    internal readonly record struct Corner(Point A, Point B, Point C);
    private readonly Dictionary<(int, int, int), Corner[]> _corners;
    private readonly Dictionary<(int, int), Corner[]> _orientedCorners;
    public ScanStructureIndex Distances { get; }
    public int Width { get; }
    public int Height { get; }
    public bool HasGeometry => _corners.Count > 0;

    private DeepScanStructureIndex(Mat line)
    {
        Width = line.Width;
        Height = line.Height;
        Distances = ScanStructureIndex.Get(line);
        using var copy = line.Clone();
        Cv2.FindContours(copy, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
        var corners = ExtractCorners(contours);
        _corners = corners.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToArray());
        _orientedCorners = corners.GroupBy(c => (Angle(c.A, c.B), Angle(c.C, c.B)))
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    public static void Prewarm(Mat line) => Cache.GetValue(line, m => new(m));
    public static bool TryGet(Mat line, out DeepScanStructureIndex? index) => Cache.TryGetValue(line, out index);

    internal static Corner[] SelectQueryCorners(IEnumerable<Point[]> contours, int width, int height) =>
        ExtractCorners(contours)
            // Image clipping is not an authored corner or a measured wall length.
            .Where(c => InsideBounds(c.A, width, height) && InsideBounds(c.B, width, height)
                && InsideBounds(c.C, width, height))
            .OrderByDescending(c => Math.Min(Length(c.A, c.B), Length(c.C, c.B)))
            // Equal angles/ratios at different positions are independent landmarks.
            // Keeping just one key can retain a fog-clipped corner and discard a full one.
            .Distinct().Take(48).ToArray();

    private static bool InsideBounds(Point p, int width, int height) =>
        p.X > 2 && p.Y > 2 && p.X < width - 3 && p.Y < height - 3;

    internal static Corner[] ExtractCorners(IEnumerable<Point[]> contours)
    {
        var result = new HashSet<Corner>();
        foreach (var contour in contours)
        {
            var polygon = Cv2.ApproxPolyDP(contour, 1.5, true);
            if (polygon.Length < 3) continue;
            for (var i = 0; i < polygon.Length; i++)
            {
                var corner = new Corner(polygon[(i + polygon.Length - 1) % polygon.Length],
                    polygon[i], polygon[(i + 1) % polygon.Length]);
                var ab = Length(corner.A, corner.B);
                var cb = Length(corner.C, corner.B);
                var cross = (double)(corner.A.X - corner.B.X) * (corner.C.Y - corner.B.Y)
                    - (double)(corner.A.Y - corner.B.Y) * (corner.C.X - corner.B.X);
                // A straight wall or tiny raster stair cannot establish a local pose.
                if (ab < 10 || cb < 10 || Math.Abs(cross) / (ab * cb) < .3) continue;
                result.Add(corner);
                result.Add(new(corner.C, corner.B, corner.A));
            }
        }
        return result.ToArray();
    }

    internal IEnumerable<Corner> Lookup(Corner live)
    {
        var (a, b, ratio) = Key(live);
        for (var da = -1; da <= 1; da++)
        for (var db = -1; db <= 1; db++)
        for (var dr = -1; dr <= 1; dr++)
            if (_corners.TryGetValue(((a + da + 72) % 72, (b + db + 72) % 72, ratio + dr), out var values))
                foreach (var value in values) yield return value;
    }

    internal IEnumerable<Corner> LookupPartial(Corner live)
    {
        var a = Angle(live.A, live.B);
        var b = Angle(live.C, live.B);
        for (var da = -1; da <= 1; da++)
        for (var db = -1; db <= 1; db++)
            if (_orientedCorners.TryGetValue(((a + da + 72) % 72, (b + db + 72) % 72), out var values))
                foreach (var value in values) yield return value;
    }

    internal static bool TryPartialPose(Corner reference, Corner live, ScanExecutionPolicy policy,
        out double scale, out double x, out double y)
    {
        // Only BA must have both endpoints visible. BC supplies its direction;
        // fog may truncate it, so its apparent length must not determine scale.
        // This is a proposal, still subject to all-point identity verification.
        var rx = reference.A.X - reference.B.X;
        var ry = reference.A.Y - reference.B.Y;
        var lx = live.A.X - live.B.X;
        var ly = live.A.Y - live.B.Y;
        scale = ((double)rx * lx + (double)ry * ly) / ((double)rx * rx + (double)ry * ry);
        x = live.B.X - scale * reference.B.X;
        y = live.B.Y - scale * reference.B.Y;
        return double.IsFinite(scale) && scale >= policy.MinimumScale && scale <= policy.MaximumScale
            && Residual(reference.A, live.A, scale, x, y) <= 3;
    }

    internal static bool TryCornerPairPose(Corner reference, Corner referenceOther,
        Corner live, Corner liveOther, ScanExecutionPolicy policy,
        out double scale, out double x, out double y)
    {
        // Two visible bends measure their separation even when every wall arm
        // ends at fog or an icon mask. Truncated arm endpoints never set scale.
        var rx = referenceOther.B.X - reference.B.X;
        var ry = referenceOther.B.Y - reference.B.Y;
        var lx = liveOther.B.X - live.B.X;
        var ly = liveOther.B.Y - live.B.Y;
        var lengthSquared = (double)rx * rx + (double)ry * ry;
        scale = lengthSquared >= 100 ? ((double)rx * lx + (double)ry * ly) / lengthSquared : double.NaN;
        x = y = 0;
        if (!double.IsFinite(scale) || scale < policy.MinimumScale || scale > policy.MaximumScale) return false;
        var errorX = rx * scale - lx;
        var errorY = ry * scale - ly;
        if (errorX * errorX + errorY * errorY > 4) return false;
        x = live.B.X - scale * reference.B.X;
        y = live.B.Y - scale * reference.B.Y;
        return true;
    }

    private static (int, int, int) Key(Corner c) =>
        (Angle(c.A, c.B), Angle(c.C, c.B), (int)Math.Floor(Math.Log(Length(c.A, c.B) / Length(c.C, c.B)) / RatioStep));
    private static int Angle(Point p, Point origin) =>
        ((int)Math.Floor((Math.Atan2(p.Y - origin.Y, p.X - origin.X) + Math.PI) / AngleStep)) % 72;
    private static double Length(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    internal static bool TryPose(Corner reference, Corner live, ScanExecutionPolicy policy,
        out double scale, out double x, out double y)
    {
        var rx = (reference.A.X + reference.B.X + reference.C.X) / 3d;
        var ry = (reference.A.Y + reference.B.Y + reference.C.Y) / 3d;
        var lx = (live.A.X + live.B.X + live.C.X) / 3d;
        var ly = (live.A.Y + live.B.Y + live.C.Y) / 3d;
        var numerator = 0d;
        var denominator = 0d;
        Accumulate(reference.A, live.A); Accumulate(reference.B, live.B); Accumulate(reference.C, live.C);
        scale = numerator / denominator;
        x = lx - scale * rx;
        y = ly - scale * ry;
        if (!double.IsFinite(scale) || scale < policy.MinimumScale || scale > policy.MaximumScale) return false;
        return Residual(reference.A, live.A, scale, x, y) <= 3
            && Residual(reference.B, live.B, scale, x, y) <= 3
            && Residual(reference.C, live.C, scale, x, y) <= 3;

        void Accumulate(Point r, Point l)
        {
            numerator += (r.X - rx) * (l.X - lx) + (r.Y - ry) * (l.Y - ly);
            denominator += (r.X - rx) * (r.X - rx) + (r.Y - ry) * (r.Y - ry);
        }
    }

    private static double Residual(Point r, Point l, double scale, double x, double y) =>
        Math.Sqrt(Math.Pow(r.X * scale + x - l.X, 2) + Math.Pow(r.Y * scale + y - l.Y, 2));
}
