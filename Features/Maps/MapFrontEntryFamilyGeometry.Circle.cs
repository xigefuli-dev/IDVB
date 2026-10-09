using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public static partial class MapFrontEntryFamilyGeometry
{
    // Bounds use outward binary64 rounding. Bounds never decide positive
    // acceptance: the original-coordinate Hypot predicate does that separately.
    private readonly record struct Bounds(double Lower, double Upper)
    {
        public bool Finite => double.IsFinite(Lower) && double.IsFinite(Upper) && Lower <= Upper;
        public double MaximumAbsolute => Math.Max(Math.Abs(Lower), Math.Abs(Upper));
        public static Bounds Exact(double value) => new(value, value);
        public static Bounds Add(Bounds a, Bounds b) => new(Down(a.Lower + b.Lower), Up(a.Upper + b.Upper));
        public static Bounds Subtract(Bounds a, Bounds b) => new(Down(a.Lower - b.Upper), Up(a.Upper - b.Lower));
        public static Bounds Multiply(Bounds a, Bounds b)
        {
            var values = new[] { a.Lower * b.Lower, a.Lower * b.Upper, a.Upper * b.Lower, a.Upper * b.Upper };
            return new(Down(values.Min()), Up(values.Max()));
        }
        public static Bounds Square(Bounds a)
        {
            var x = a.Lower * a.Lower; var y = a.Upper * a.Upper;
            return new(a.Lower <= 0 && a.Upper >= 0 ? 0 : Math.Max(0, Down(Math.Min(x, y))), Up(Math.Max(x, y)));
        }
        public static Bounds DividePositive(Bounds a, Bounds b) => b.Lower > 0
            ? new(Down(a.Lower / b.Upper), Up(a.Upper / b.Lower)) : new(double.NaN, double.NaN);
        public static Bounds NormSquared(Bounds x, Bounds y) => Add(Square(x), Square(y));
    }

    private sealed class Frame
    {
        public Point2d[] Source { get; }
        public Point2d[] Query { get; }
        public Frame(Point2d[] source, Point2d[] query) { Source = source; Query = query; }
        public bool Finite => Source.Concat(Query).All(FinitePoint);
        public (Bounds X, Bounds Y) SourceDelta(int i) => (
            Bounds.Subtract(Bounds.Exact(Source[i].X), Bounds.Exact(Source[0].X)),
            Bounds.Subtract(Bounds.Exact(Source[i].Y), Bounds.Exact(Source[0].Y)));
        public (Bounds X, Bounds Y) CenterBounds(int i, double scale)
        {
            if (i == 0) return (Bounds.Exact(0), Bounds.Exact(0));
            var p = SourceDelta(i); var s = Bounds.Exact(scale);
            return (Bounds.Subtract(Bounds.Subtract(Bounds.Exact(Query[i].X), Bounds.Exact(Query[0].X)), Bounds.Multiply(s, p.X)),
                Bounds.Subtract(Bounds.Subtract(Bounds.Exact(Query[i].Y), Bounds.Exact(Query[0].Y)), Bounds.Multiply(s, p.Y)));
        }
        public double? LipschitzUpperBound()
        {
            double maximum = 0;
            for (var i = 1; i < 3; i++)
            {
                if (Source[i].X == Source[0].X && Source[i].Y == Source[0].Y) continue;
                var p = SourceDelta(i); var squared = Bounds.NormSquared(p.X, p.Y);
                if (!squared.Finite || squared.Upper < 0) return null;
                maximum = Math.Max(maximum, SqrtUp(squared.Upper));
            }
            return double.IsFinite(maximum) ? maximum : null;
        }
    }

    private static MapFrontEntryScaleSample Sample(Frame frame, double scale)
    {
        if (!frame.Finite || !double.IsFinite(scale) || scale < 0)
            return new() { Scale = scale, MecRadius = double.NaN };
        var centers = new Point2d[3]; var intervals = new (Bounds X, Bounds Y)[3];
        for (var i = 0; i < 3; i++)
        {
            centers[i] = new((frame.Query[i].X - frame.Query[0].X) - scale * (frame.Source[i].X - frame.Source[0].X),
                (frame.Query[i].Y - frame.Query[0].Y) - scale * (frame.Source[i].Y - frame.Source[0].Y));
            intervals[i] = frame.CenterBounds(i, scale);
        }
        var circle = MinimumCircle(centers);
        var translation = new Point2d(frame.Query[0].X - scale * frame.Source[0].X + circle.Center.X,
            frame.Query[0].Y - scale * frame.Source[0].Y + circle.Center.Y);
        var residuals = frame.Source.Select((p, i) => double.Hypot(
            p.X * scale + translation.X - frame.Query[i].X,
            p.Y * scale + translation.Y - frame.Query[i].Y)).ToArray();
        var finite = centers.All(FinitePoint) && FinitePoint(circle.Center) && FinitePoint(translation)
            && double.IsFinite(circle.Radius) && residuals.All(double.IsFinite);
        var bounds = CircleRadiusBounds(intervals, circle.Center);
        return new()
        {
            Scale = scale, CenteredTranslation = circle.Center, Translation = translation, MecRadius = circle.Radius,
            RadiusLowerBound = bounds.Lower, RadiusUpperBound = bounds.Upper,
            OriginalPhysicalResiduals = Array.AsReadOnly(residuals), ArithmeticFinite = finite,
            OriginalPhysical3Verified = scale > 0 && finite && residuals.All(r => r <= PhysicalTolerancePixels)
        };
    }

    private static (Point2d Center, double Radius) MinimumCircle(Point2d[] points)
    {
        var best = new Point2d(double.NaN, double.NaN); var radius = double.PositiveInfinity;
        void Consider(Point2d center)
        {
            if (!FinitePoint(center)) return;
            double current = 0;
            foreach (var point in points) current = Math.Max(current, double.Hypot(point.X - center.X, point.Y - center.Y));
            if (double.IsFinite(current) && current < radius) { best = center; radius = current; }
        }
        foreach (var point in points) Consider(point);
        for (var i = 0; i < 3; i++) for (var j = i + 1; j < 3; j++)
            Consider(new(points[i].X / 2 + points[j].X / 2, points[i].Y / 2 + points[j].Y / 2));
        var ux = points[1].X - points[0].X; var uy = points[1].Y - points[0].Y;
        var vx = points[2].X - points[0].X; var vy = points[2].Y - points[0].Y;
        var unit = Math.Max(Math.Max(Math.Abs(ux), Math.Abs(uy)), Math.Max(Math.Abs(vx), Math.Abs(vy)));
        if (unit > 0 && double.IsFinite(unit))
        {
            ux /= unit; uy /= unit; vx /= unit; vy /= unit;
            var determinant = 2 * (ux * vy - uy * vx);
            if (determinant != 0 && double.IsFinite(determinant))
            {
                var uSquared = ux * ux + uy * uy; var vSquared = vx * vx + vy * vy;
                Consider(new(points[0].X + ((uSquared * vy - vSquared * uy) / determinant) * unit,
                    points[0].Y + ((ux * vSquared - vx * uSquared) / determinant) * unit));
            }
        }
        return (best, radius);
    }

    private static (double? Lower, double? Upper) CircleRadiusBounds((Bounds X, Bounds Y)[] points, Point2d center)
    {
        if (!FinitePoint(center) || points.Any(p => !p.X.Finite || !p.Y.Finite)) return (null, null);
        var sides = new Bounds[3]; var index = 0; double lowerSquared = 0, upperSquared = 0;
        for (var i = 0; i < 3; i++)
        {
            var squared = Bounds.NormSquared(Bounds.Subtract(points[i].X, Bounds.Exact(center.X)),
                Bounds.Subtract(points[i].Y, Bounds.Exact(center.Y)));
            if (!squared.Finite) return (null, null);
            upperSquared = Math.Max(upperSquared, squared.Upper);
            for (var j = i + 1; j < 3; j++)
            {
                sides[index] = Bounds.NormSquared(Bounds.Subtract(points[i].X, points[j].X), Bounds.Subtract(points[i].Y, points[j].Y));
                if (!sides[index].Finite) return (null, null);
                lowerSquared = Math.Max(lowerSquared, Math.Max(0, Down(sides[index].Lower / 4))); index++;
            }
        }
        // A proved acute triangle has its circumcircle as the MEC. If angle or
        // determinant bounds straddle a boundary, only the diameter bound is used.
        var acute = Enumerable.Range(0, 3).All(i => sides[i].Upper <
            Bounds.Add(sides[(i + 1) % 3], sides[(i + 2) % 3]).Lower);
        if (acute)
        {
            var ux = Bounds.Subtract(points[1].X, points[0].X); var uy = Bounds.Subtract(points[1].Y, points[0].Y);
            var vx = Bounds.Subtract(points[2].X, points[0].X); var vy = Bounds.Subtract(points[2].Y, points[0].Y);
            var cross = Bounds.Subtract(Bounds.Multiply(ux, vy), Bounds.Multiply(uy, vx));
            var denominator = Bounds.Multiply(Bounds.Exact(4), Bounds.Square(cross));
            var numerator = Bounds.Multiply(Bounds.Multiply(sides[0], sides[1]), sides[2]);
            var squared = Bounds.DividePositive(numerator, denominator);
            if (squared.Finite) lowerSquared = Math.Max(lowerSquared, Math.Max(0, squared.Lower));
        }
        var lower = SqrtDown(lowerSquared); var upper = SqrtUp(Math.Max(0, upperSquared));
        return double.IsFinite(lower) && double.IsFinite(upper) && lower <= upper ? (lower, upper) : (null, null);
    }

    private static double? ArithmeticAllowance(Frame frame, MapFrontEntryScaleBracket domain)
    {
        var scales = new Bounds(domain.Minimum, domain.Maximum); double maximum = 0;
        for (var i = 0; i < 3; i++)
        {
            double Axis(double source, double query)
            {
                var product = Bounds.Multiply(scales, Bounds.Exact(source));
                if (!product.Finite) return double.NaN;
                // For a possible original Hypot<=3 model each rounded residual
                // component is <=3. Its final projected coordinate is therefore
                // bounded by |query|+4; the extra unit bounds subtraction rounding.
                var additionMagnitude = Up(Math.Abs(query) + 4);
                return Up(Up(UlpUpper(product.MaximumAbsolute) + UlpUpper(additionMagnitude)) + UlpUpper(4));
            }
            var x = Axis(frame.Source[i].X, frame.Query[i].X); var y = Axis(frame.Source[i].Y, frame.Query[i].Y);
            var norm = Bounds.NormSquared(Bounds.Exact(x), Bounds.Exact(y));
            if (!double.IsFinite(x) || !double.IsFinite(y) || !norm.Finite) return null;
            // This norm-rounding allowance weakens negative certificates only;
            // it is never added to the accepted physical residual threshold.
            maximum = Math.Max(maximum, Up(SqrtUp(norm.Upper) + Up(8 * UlpUpper(4))));
        }
        return double.IsFinite(maximum) ? maximum : null;
    }
    private static double UlpUpper(double magnitude) => magnitude >= 0 && double.IsFinite(magnitude)
        ? Up(Math.BitIncrement(magnitude) - magnitude) : double.NaN;
    private static double Down(double value) => Math.BitDecrement(value);
    private static double Up(double value) => Math.BitIncrement(value);
    private static double SqrtDown(double value) => value == 0 ? 0 : Math.Max(0, Down(Math.Sqrt(value)));
    private static double SqrtUp(double value) => Up(Math.Sqrt(value));
    private static bool FinitePoint(Point2d point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
