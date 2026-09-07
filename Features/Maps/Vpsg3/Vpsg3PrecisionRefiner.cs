using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record Vpsg3PrecisionResiduals(
    double Loss, double NearP95, double FarP95, double[] PartitionP95);

public sealed record Vpsg3PrecisionResult(
    double SeedScale, double Scale, double OffsetX, double OffsetY,
    Vpsg3PrecisionResiduals? Before, Vpsg3PrecisionResiduals? After,
    int Iterations, int Probes, double Milliseconds, string Termination,
    bool Calibrated, double RadiusPixels);

/// <summary>A shared deadline for ALL extra work on a frame, including competitors and verification.</summary>
internal readonly record struct Vpsg3PrecisionBudget(long Started)
{
    internal const double MaximumMilliseconds = 10d;
    internal static Vpsg3PrecisionBudget Start() => new(Stopwatch.GetTimestamp());
    internal double Elapsed => Stopwatch.GetElapsedTime(Started).TotalMilliseconds;
    internal bool Expired => Elapsed >= MaximumMilliseconds;
}

/// <summary>Shadow-only continuous scale/translation refinement. Never commits a transform or cache.</summary>
internal static class Vpsg3PrecisionRefiner
{
    internal static Vpsg3PrecisionResult Refine(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, double seedScale, double seedX, double seedY,
        Vpsg3PrecisionBudget budget, bool lockScale = false)
    {
        var started = Stopwatch.GetTimestamp();
        var probes = 0;
        var rounds = 0;
        var scale = seedScale;
        var dx = 0d;
        var dy = 0d;
        var radius = 0d;
        Vpsg3PrecisionResiduals? before = null;
        Vpsg3PrecisionResiduals? after = null;
        var cx = observation.Width / 2d;
        var cy = observation.Height / 2d;
        var centerX = observation.ViewportBounds.X + cx;
        var centerY = observation.ViewportBounds.Y + cy;
        if (!double.IsFinite(seedScale) || seedScale <= 0 || !double.IsFinite(seedX) || !double.IsFinite(seedY))
            return Finish("invalid-seed");
        var rcx = (centerX - seedX) / seedScale;
        var rcy = (centerY - seedY) / seedScale;
        if (budget.Expired) return Finish("budget");
        if (floor.IsDisposed || floor.PrecisionDistance.IsEmpty) return Finish("distance-unavailable");

        // Freeze the valid support once. Moving outside the reference incurs a penalty, never removal.
        var points = observation.SparseEdgePoints.Where(p =>
            p.X >= 0 && p.Y >= 0 && p.X < observation.Width && p.Y < observation.Height
            && observation.ValidMask.At<byte>(p.Y, p.X) >= 128).ToArray();
        var counts = new int[4];
        var partitions = new int[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            partitions[i] = (p.X < cx ? 0 : 1) + (p.Y < cy ? 0 : 2);
            counts[partitions[i]]++;
            radius = Math.Max(radius, double.Hypot(p.X - cx, p.Y - cy));
        }
        var hasOrthogonalSpan = (counts[0] + counts[2] > 0 && counts[1] + counts[3] > 0)
            && (counts[0] + counts[1] > 0 && counts[2] + counts[3] > 0);
        if (points.Length < 35 || counts.Count(n => n >= 15) < 2 || !hasOrthogonalSpan || radius < 80d)
            return Finish("insufficient-support");
        if (budget.Expired) return Finish("budget");
        before = Residuals(scale, dx, dy);
        if (before.Loss >= 5.0d) return Finish("out-of-reference");
        var best = before.Loss;
        var ds = seedScale * 0.005d;
        var dt = 1d;
        var converged = false;
        var minSi = lockScale ? 0 : -1;
        var maxSi = lockScale ? 0 : 1;
        for (; rounds < 12; rounds++)
        {
            if (budget.Expired) return Finish("budget");
            if (dt <= 0.125d && (lockScale || ds / seedScale * radius <= 0.25d))
            {
                converged = true;
                break;
            }
            var nextS = scale;
            var nextX = dx;
            var nextY = dy;
            for (var si = minSi; si <= maxSi; si++)
            for (var xi = -1; xi <= 1; xi++)
            for (var yi = -1; yi <= 1; yi++)
            {
                if (si == 0 && xi == 0 && yi == 0) continue;
                if (budget.Expired) return Finish("budget");
                var s = scale + si * ds;
                var x = dx + xi * dt;
                var y = dy + yi * dt;
                if (!lockScale && Math.Abs(s / seedScale - 1) > 0.030000001d || Math.Abs(x) > 4.0000001d || Math.Abs(y) > 4.0000001d)
                    continue;
                var loss = Loss(s, x, y);
                probes++;
                if (loss < best - 1e-9d)
                {
                    best = loss;
                    nextS = s;
                    nextX = x;
                    nextY = y;
                }
            }
            if (nextS == scale && nextX == dx && nextY == dy) { ds /= 2d; dt /= 2d; }
            scale = lockScale ? seedScale : nextS;
            dx = nextX;
            dy = nextY;
        }
        if (budget.Expired) return Finish("budget");
        if (!converged) return Finish("iterations");
        if ((!lockScale && Math.Abs(scale / seedScale - 1) >= 0.029999d) || Math.Abs(dx) >= 3.999999d || Math.Abs(dy) >= 3.999999d)
            return Finish("boundary");

        if (!lockScale)
        {
            // A small step is not evidence of scale observability. Profile both scales that move
            // the far support by 1px, allowing translation to compensate at each competing scale.
            var uncertaintyStep = scale / radius;
            foreach (var sign in new[] { -1, 1 })
            {
                var competitor = double.PositiveInfinity;
                for (var xi = -2; xi <= 2; xi++)
                for (var yi = -2; yi <= 2; yi++)
                {
                    if (budget.Expired) return Finish("budget");
                    competitor = Math.Min(competitor, Loss(scale + sign * uncertaintyStep,
                        dx + xi * 0.5d, dy + yi * 0.5d));
                    probes++;
                }
                if (competitor <= best + 1e-4d) return Finish("scale-unobservable");
            }
        }
        after = Residuals(scale, dx, dy);
        return Finish(budget.Expired ? "budget" : "converged");

        Vpsg3PrecisionResult Finish(string reason) => new(seedScale, scale,
            double.IsFinite(seedScale) && seedScale > 0 ? centerX - (centerX - seedX) / seedScale * scale + dx : seedX,
            double.IsFinite(seedScale) && seedScale > 0 ? centerY - (centerY - seedY) / seedScale * scale + dy : seedY,
            before, after, rounds, probes, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            reason, reason == "converged", radius);

        double Distance(Point p, double s, double x, double y)
        {
            var rx = rcx + (p.X - cx - x) / s;
            var ry = rcy + (p.Y - cy - y) / s;
            return Math.Min(6d, Sample(floor.PrecisionDistance, floor.ReferenceWidth,
                floor.ReferenceHeight, rx, ry) * s);
        }

        double Loss(double s, double x, double y)
        {
            Span<double> sums = stackalloc double[4];
            sums.Clear();
            var totalSum = 0d;
            for (var i = 0; i < points.Length; i++)
            {
                var d = Distance(points[i], s, x, y);
                var huber = d <= 1.5d ? 0.5d * d * d : 1.5d * (d - 0.75d);
                sums[partitions[i]] += huber;
                totalSum += huber;
            }
            var loss = 0d;
            var active = 0;
            for (var i = 0; i < 4; i++)
                if (counts[i] >= 15) { loss += sums[i] / counts[i]; active++; }
            return active > 0 ? loss / active : totalSum / points.Length;
        }

        Vpsg3PrecisionResiduals Residuals(double s, double x, double y)
        {
            var near = new List<double>();
            var far = new List<double>();
            var quadrants = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>() };
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var d = Distance(p, s, x, y);
                quadrants[partitions[i]].Add(d);
                var r = double.Hypot(p.X - cx, p.Y - cy);
                if (r <= radius * 0.35d) near.Add(d);
                if (r >= radius * 0.70d) far.Add(d);
            }
            return new(Loss(s, x, y), Percentile(near), Percentile(far), quadrants.Select(Percentile).ToArray());
        }
    }

    private static double Percentile(List<double> values)
    {
        if (values.Count == 0) return -1d; // Explicitly missing, never a fabricated zero residual.
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * 0.95d) - 1];
    }

    internal static double Sample(ReadOnlySpan<float> field, int width, int height, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > width - 1 || y > height - 1)
            return double.PositiveInfinity;
        var ix = (int)x;
        var iy = (int)y;
        var nx = Math.Min(ix + 1, width - 1);
        var ny = Math.Min(iy + 1, height - 1);
        var fx = x - ix;
        var fy = y - iy;
        return (field[iy * width + ix] * (1 - fx) + field[iy * width + nx] * fx) * (1 - fy)
            + (field[ny * width + ix] * (1 - fx) + field[ny * width + nx] * fx) * fy;
    }
}
