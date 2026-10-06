using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapLocalGeometrySolver
{
    // Only this pure bounded helper bypasses the first compilation tier. It
    // gives up tier-dependent PGO; cold end-to-end performance still needs
    // measurement. Budget/cancellation observation belongs to the array caller.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryMinimumSixCornerEnclosingCircleSimd(
        Point2d[] points, out (Point2d Center, double Radius) result)
    {
        result = default;
        if (points.Length != 6 || !Avx2.IsSupported) return false;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || IsSixCircleSimdExtreme(point.X) || IsSixCircleSimdExtreme(point.Y))
                return false;
        }

        // Preserve the original interleaved point/pair/triple center order and
        // every formula. Partial zero determinants have no original center.
        Span<Point2d> centers = stackalloc Point2d[41];
        var centerCount = 0;
        var skippedTriples = 0;
        for (var i = 0; i < points.Length; i++)
        {
            centers[centerCount++] = points[i];
            for (var j = i + 1; j < points.Length; j++)
            {
                centers[centerCount++] = (points[i] + points[j]) * .5;
                for (var k = j + 1; k < points.Length; k++)
                {
                    var u = points[j] - points[i];
                    var v = points[k] - points[i];
                    var determinant = 2 * (u.X * v.Y - u.Y * v.X);
                    if (determinant == 0)
                    {
                        skippedTriples++;
                        continue;
                    }
                    var uu = Dot(u, u);
                    var vv = Dot(v, v);
                    var center = points[i] + new Point2d(
                        (v.Y * uu - u.Y * vv) / determinant,
                        (u.X * vv - v.X * uu) / determinant);
                    if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)
                        || IsSixCircleSimdExtreme(center.X) || IsSixCircleSimdExtreme(center.Y))
                        return false;
                    centers[centerCount++] = center;
                }
            }
        }
        if (skippedTriples == 20) return false;

        var bestCenter = points[0];
        var bestSquared = double.PositiveInfinity;
        for (var start = 0; start < centerCount; start += 4)
        {
            var lanes = Math.Min(4, centerCount - start);
            var c0 = centers[start];
            var c1 = centers[start + (lanes > 1 ? 1 : 0)];
            var c2 = centers[start + (lanes > 2 ? 2 : 0)];
            var c3 = centers[start + (lanes > 3 ? 3 : 0)];
            var xs = Vector256.Create(c0.X, c1.X, c2.X, c3.X);
            var ys = Vector256.Create(c0.Y, c1.Y, c2.Y, c3.Y);
            var maximum = Vector256<double>.Zero;
            for (var index = 0; index < 6; index++)
            {
                var dx = Avx.Subtract(Vector256.Create(points[index].X), xs);
                var dy = Avx.Subtract(Vector256.Create(points[index].Y), ys);
                // Keep the two multiply roundings and subsequent add; no FMA.
                var squared = Avx.Add(Avx.Multiply(dx, dx), Avx.Multiply(dy, dy));
                // The route bounds guarantee finite nonnegative squares.
                // Their maximum has the same bits as the scalar Math.Max.
                maximum = Avx.Max(maximum, squared);
            }
            for (var lane = 0; lane < lanes; lane++)
            {
                var squared = maximum.GetElement(lane);
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    bestCenter = centers[start + lane];
                }
            }
        }
        result = (bestCenter, Math.Sqrt(bestSquared));
        return true;
    }

    private static bool IsSixCircleSimdExtreme(double value)
    {
        var absolute = Math.Abs(value);
        // Only choose the arithmetic implementation here. These conservative
        // bounds avoid overflow/subnormal concerns; they never reject a fit.
        return absolute > 1e100 || (absolute != 0 && absolute < 1e-100);
    }
}
