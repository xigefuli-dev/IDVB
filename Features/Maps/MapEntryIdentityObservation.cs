using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>One captured viewport in its original pixel space. No candidate affects extraction.</summary>
internal sealed partial class MapEntryIdentityFrame : IDisposable
{
    public Mat Floor { get; }
    public Mat Query { get; }
    public Mat Observed { get; }
    public Mat AuthorObserved { get; }
    public IReadOnlyList<MapEntryIdentityWall> Walls { get; }
    public Rect Bounds { get; }
    private readonly Mat _bgr;

    public MapEntryIdentityFrame(Mat source, Func<bool> canCompute)
    {
        _bgr = new Mat();
        if (source.Channels() == 4) Cv2.CvtColor(source, _bgr, ColorConversionCodes.BGRA2BGR);
        else if (source.Channels() == 1) Cv2.CvtColor(source, _bgr, ColorConversionCodes.GRAY2BGR);
        else source.CopyTo(_bgr);
        Floor = ColorMask(_bgr);
        Query = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(Floor, Query, kernel);
        (Observed, AuthorObserved) = PhysicalEdges(_bgr, Query, Floor);
        // The configured IDVB viewport is already the observation domain. Do
        // not copy the reference app's full-client fixed central crop here.
        Bounds = new Rect(0, 0, source.Width, source.Height);
        Cv2.Rectangle(Observed, Bounds, Scalar.Black, 6);
        Cv2.Rectangle(AuthorObserved, Bounds, Scalar.Black, 6);
        Walls = canCompute() ? ExtractWalls(Query, Observed) : [];
    }

    internal static Mat ColorMask(Mat bgr)
    {
        using var hsv = new Mat();
        using var neutral = new Mat();
        using var brown = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(0, 0, 68), new Scalar(179, 72, 205), neutral);
        Cv2.InRange(hsv, new Scalar(4, 22, 68), new Scalar(32, 175, 205), brown);
        var mask = new Mat();
        Cv2.BitwiseOr(neutral, brown, mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        return mask;
    }

    private static (Mat Observed, Mat AuthorObserved) PhysicalEdges(Mat bgr, Mat query, Mat floor)
    {
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var k9 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        using var edge = new Mat();
        using var authorEdge = new Mat();
        using var gray8 = new Mat();
        using var gray = new Mat();
        using var smooth = new Mat();
        using var gx = new Mat();
        using var gy = new Mat();
        using var magnitude = new Mat();
        using var maximum = new Mat();
        using var minimum = new Mat();
        using var span = new Mat();
        using var relative = new Mat();
        using var positive = new Mat();
        using var sharp = new Mat();
        using var hsv = new Mat();
        using var marker = new Mat();
        Cv2.MorphologyEx(query, edge, MorphTypes.Gradient, k3);
        Cv2.MorphologyEx(floor, authorEdge, MorphTypes.Gradient, k3);
        Cv2.CvtColor(bgr, gray8, ColorConversionCodes.BGR2GRAY);
        gray8.ConvertTo(gray, MatType.CV_32F);
        Cv2.GaussianBlur(gray, smooth, new Size(3, 3), 0);
        Cv2.Sobel(smooth, gx, MatType.CV_32F, 1, 0, scale: .125);
        Cv2.Sobel(smooth, gy, MatType.CV_32F, 0, 1, scale: .125);
        Cv2.Magnitude(gx, gy, magnitude);
        Cv2.Dilate(gray, maximum, k9);
        Cv2.Erode(gray, minimum, k9);
        Cv2.Subtract(maximum, minimum, span);
        span.ConvertTo(relative, MatType.CV_32F, .25);
        Cv2.Compare(magnitude, relative, sharp, CmpTypes.GE);
        Cv2.Compare(span, 0, positive, CmpTypes.GT);
        Cv2.BitwiseAnd(sharp, positive, sharp);
        Cv2.Dilate(sharp, sharp, k3);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(0, 88, 80), new Scalar(179, 255, 255), marker);
        Cv2.Dilate(marker, marker, k5);
        var observed = new Mat();
        var authorObserved = new Mat();
        // Both masks use the same captured pixels and sharpness calculation;
        // only the query dilation and marker exclusion differ.
        Cv2.BitwiseAnd(edge, sharp, observed);
        Cv2.BitwiseAnd(authorEdge, sharp, authorObserved);
        observed.SetTo(Scalar.Black, marker);
        return (observed, authorObserved);
    }

    internal static IReadOnlyList<MapEntryIdentityWall> ExtractWalls(Mat query, Mat observed)
    {
        Cv2.FindContours(query, out Point[][] contours, out _, RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);
        var walls = new List<MapEntryIdentityWall>();
        var edgeId = 0;
        foreach (var contour in contours)
        {
            var polygon = Cv2.ApproxPolyDP(contour, 2, true);
            if (polygon.Length < 4 || Math.Abs(Cv2.ContourArea(polygon)) < 100) continue;
            for (var i = 0; i < polygon.Length; i++, edgeId++)
            {
                var p = polygon[i];
                var end = polygon[(i + 1) % polygon.Length];
                var dx = end.X - p.X;
                var dy = end.Y - p.Y;
                var tangent = Math.Abs(dx) < Math.Abs(dy) ? 1 : 0;
                var length = Math.Abs(tangent == 0 ? dx : dy);
                if (length < 12 || Math.Abs(tangent == 0 ? dy : dx) > 3) continue;
                var run = new List<(double Normal, double Along)>();
                void Flush()
                {
                    if (run.Count >= 12)
                        walls.Add(new(1 - tangent, Math.Sign(tangent == 0 ? dx : dy),
                            Median(run.Select(v => v.Normal)), run.Min(v => v.Along),
                            run.Max(v => v.Along), edgeId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    run.Clear();
                }
                for (var j = 0; j <= length; j++)
                {
                    var x = p.X + j * dx / (double)length;
                    var y = p.Y + j * dy / (double)length;
                    var ix = (int)Math.Round(x);
                    var iy = (int)Math.Round(y);
                    if (ix >= 0 && iy >= 0 && ix < observed.Width && iy < observed.Height
                        && observed.At<byte>(iy, ix) != 0)
                        run.Add(tangent == 0 ? (y, x) : (x, y));
                    else Flush();
                }
                Flush();
            }
        }
        return walls;
    }

    public bool IsVisibleFloor(int x, int y) => x >= 0 && y >= 0
        && x < Query.Width && y < Query.Height && Query.At<byte>(y, x) != 0;

    internal static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? 0 : (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    public void Dispose()
    {
        _bgr.Dispose();
        Floor.Dispose();
        Query.Dispose();
        Observed.Dispose();
        AuthorObserved.Dispose();
    }
}
