using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    // FitSix supplies a nonempty array. Direct indexing avoids relying on the
    // JIT's interface devirtualization; candidate order and arithmetic stay exact.
    private static (Point2d Center, double Radius) MinimumCornerEnclosingCircleArray(
        Point2d[] points, CancellationToken cancellationToken)
    {
        var bestCenter = points[0];
        var bestSquared = double.PositiveInfinity;
        var pointCount = points.Length;
        // Six-point fitting has at most 41 candidate centers. Both arithmetic
        // routes are synchronous bounded operations. Observe cancellation and
        // budget around the operation; never publish after the closing check.
        var boundedSixPoints = pointCount == 6;
        if (boundedSixPoints)
        {
            CheckGeometryBudget(cancellationToken);
            if (TryMinimumSixCornerEnclosingCircleSimd(points, out var vectorResult))
            {
                CheckGeometryBudget(cancellationToken);
                return vectorResult;
            }
        }
        void Consider(Point2d center)
        {
            if (!boundedSixPoints) CheckGeometryBudget(cancellationToken);
            if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)) return;
            var maximum = 0d;
            for (var index = 0; index < pointCount; index++)
            {
                var delta = points[index] - center;
                maximum = Math.Max(maximum, Dot(delta, delta));
                if (maximum >= bestSquared) return;
            }
            if (maximum < bestSquared)
            {
                bestSquared = maximum;
                bestCenter = center;
            }
        }
        for (var i = 0; i < pointCount; i++)
        {
            Consider(points[i]);
            for (var j = i + 1; j < pointCount; j++)
            {
                Consider((points[i] + points[j]) * .5);
                for (var k = j + 1; k < pointCount; k++)
                {
                    var u = points[j] - points[i];
                    var v = points[k] - points[i];
                    var determinant = 2 * (u.X * v.Y - u.Y * v.X);
                    if (determinant == 0) continue;
                    var uu = Dot(u, u);
                    var vv = Dot(v, v);
                    Consider(points[i] + new Point2d(
                        (v.Y * uu - u.Y * vv) / determinant,
                        (u.X * vv - v.X * uu) / determinant));
                }
            }
        }
        var result = (bestCenter, Math.Sqrt(bestSquared));
        if (boundedSixPoints) CheckGeometryBudget(cancellationToken);
        return result;
    }
}
