using OpenCvSharp;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Viewport-Prior Scale Graph estimator. Descriptor matches identify graph
/// vertices; ratios between matched graph-edge lengths vote for scale without
/// requiring translation or trusting a previous floor's scale.
/// </summary>
public sealed partial class MapVpsgScaleEstimator
{

    private static double PointSpan(IEnumerable<Point2f> points)
    {
        var array = points.ToArray();
        if (array.Length == 0)
            return 0d;
        var width = array.Max(point => point.X) - array.Min(point => point.X);
        var height = array.Max(point => point.Y) - array.Min(point => point.Y);
        return Math.Sqrt((width * width) + (height * height));
    }

    private static double SquaredDistance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180d)
            angle -= 360d;
        while (angle < -180d)
            angle += 360d;
        return angle;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return double.PositiveInfinity;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}
