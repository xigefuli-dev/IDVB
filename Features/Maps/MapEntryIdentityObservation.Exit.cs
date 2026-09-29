using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapEntryIdentityFrame
{
    private sealed record WallCluster(double Coordinate, List<(int Start, int End)> Intervals, double Support);
    private sealed record ExitRectangle(Rect Bounds, bool Horizontal, double Score,
        double WallSupport, double ColorConsistency, double DoorPosition);

    public IReadOnlyList<MapEntryIdentityObservation> ObserveExits(
        IReadOnlyList<GateDetection> gates, MapScreenRect viewport, Func<bool> canCompute)
    {
        var observations = new List<MapEntryIdentityObservation>();
        using var gradient = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(Floor, gradient, MorphTypes.Gradient, kernel);
        foreach (var gate in gates)
        {
            if (!canCompute()) break;
            var center = new MapEntryIdentityPoint(gate.ScreenBounds.CenterX - viewport.X,
                gate.ScreenBounds.CenterY - viewport.Y);
            if (gate.Score < .58 || center.X < 0 || center.Y < 0
                || center.X >= Floor.Width || center.Y >= Floor.Height) continue;
            var size = Math.Max(gate.ScreenBounds.Width, gate.ScreenBounds.Height);
            var rectangles = new List<ExitRectangle>();
            foreach (var horizontal in new[] { true, false })
            {
                var walls = AxisWallClusters(gradient, horizontal, size);
                for (var i = 0; i < walls.Count; i++)
                for (var j = i + 1; j < walls.Count; j++)
                {
                    if (!canCompute()) return observations;
                    var first = walls[i];
                    var second = walls[j];
                    var a = Math.Min(first.Coordinate, second.Coordinate);
                    var b = Math.Max(first.Coordinate, second.Coordinate);
                    var gap = b - a;
                    var cross = horizontal ? center.Y : center.X;
                    var along = horizontal ? center.X : center.Y;
                    if (gap < Math.Max(10, size * .65) || gap > size * 9
                        || cross < a - size * .55 || cross > b + size * .55) continue;
                    var start = Math.Min(first.Intervals.Min(v => v.Start), second.Intervals.Min(v => v.Start));
                    var end = Math.Max(first.Intervals.Max(v => v.End), second.Intervals.Max(v => v.End));
                    if (end - start < Math.Max(14, gap * .8)
                        || along < start - size * .7 || along > end + size * .7) continue;
                    var span = FloorColorSpan(horizontal, (int)a, (int)b, start, end, along, size);
                    var left = Math.Min(span.Start, (int)Math.Round(along - size * .52));
                    var right = Math.Max(span.End, (int)Math.Round(along + size * .52));
                    var length = right - left;
                    if (length < gap * 1.02) continue;
                    var normalized = (along - left) / length;
                    var endpoint = Math.Abs(normalized - .5) * 2;
                    if (endpoint < .28) continue;
                    var support = Math.Min(1, (first.Support + second.Support) / (2 * (end - start)));
                    var aspect = length / gap;
                    var coverage = Math.Min(1, length / (double)(horizontal ? Floor.Width : Floor.Height));
                    var centered = Math.Max(0, 1 - Math.Abs(cross - (a + b) / 2) / Math.Max(gap * .5, 1));
                    var score = support * .22 + span.Consistency * .18 + Math.Min(endpoint, 1) * .2
                        + Math.Min(aspect, 4) / 4 * .08 + coverage * .2 + centered * .12;
                    var top = (int)Math.Min(a, Math.Round(cross - size * .15));
                    var bottom = (int)Math.Max(b, Math.Round(cross + size * .15));
                    var rect = horizontal ? new Rect(left, top, right - left, bottom - top)
                        : new Rect(top, left, bottom - top, right - left);
                    rectangles.Add(new(rect, horizontal, score, support, span.Consistency, normalized));
                }
            }
            var best = rectangles.OrderByDescending(r => r.Score).FirstOrDefault();
            // A visible-envelope direction alone does not measure the local
            // width required by the passport. Retain unresolved exits without
            // guessing their rectangle or using an author icon's size as unit.
            var reliable = best is { Score: >= .63, WallSupport: >= .44, ColorConsistency: >= .85 }
                && Math.Abs(best.DoorPosition - .5) >= .18;
            var direction = !reliable ? MapEntryIdentityDirection.Unknown : best!.Horizontal
                ? best.DoorPosition >= .64 ? MapEntryIdentityDirection.East
                    : best.DoorPosition <= .36 ? MapEntryIdentityDirection.West : MapEntryIdentityDirection.Unknown
                : best.DoorPosition >= .64 ? MapEntryIdentityDirection.South
                    : best.DoorPosition <= .36 ? MapEntryIdentityDirection.North : MapEntryIdentityDirection.Unknown;
            observations.Add(new(center, gate.Score, direction,
                reliable && direction != MapEntryIdentityDirection.Unknown,
                best is null ? null : new(best.Bounds.Left, best.Bounds.Top, best.Bounds.Right, best.Bounds.Bottom)));
        }
        return observations;
    }

    private static List<WallCluster> AxisWallClusters(Mat edge, bool horizontal, double size)
    {
        var minimum = Math.Max(12, (int)Math.Round(size * .7));
        var lines = Cv2.HoughLinesP(edge, 1, Math.PI / 360, Math.Max(10, (int)Math.Round(size * .55)),
            minimum, Math.Max(8, Math.Round(size * .65)));
        var raw = new List<(double Coordinate, int Start, int End)>();
        foreach (var line in lines)
        {
            var dx = Math.Abs(line.P2.X - line.P1.X);
            var dy = Math.Abs(line.P2.Y - line.P1.Y);
            if (horizontal && dx >= Math.Max(minimum, dy * 5))
                raw.Add((Math.Round((line.P1.Y + line.P2.Y) / 2d), Math.Min(line.P1.X, line.P2.X), Math.Max(line.P1.X, line.P2.X)));
            else if (!horizontal && dy >= Math.Max(minimum, dx * 5))
                raw.Add((Math.Round((line.P1.X + line.P2.X) / 2d), Math.Min(line.P1.Y, line.P2.Y), Math.Max(line.P1.Y, line.P2.Y)));
        }
        var tolerance = Math.Max(2, (int)Math.Round(size * .13));
        var groups = new List<List<(double Coordinate, int Start, int End)>>();
        foreach (var line in raw.OrderBy(v => v.Coordinate).ThenBy(v => v.Start).ThenBy(v => v.End))
        {
            var nearest = groups.OrderBy(g => Math.Abs(Median(g.Select(v => v.Coordinate)) - line.Coordinate)).FirstOrDefault();
            if (nearest is null || Math.Abs(Median(nearest.Select(v => v.Coordinate)) - line.Coordinate) > tolerance)
                groups.Add([line]);
            else nearest.Add(line);
        }
        return groups.Select(group =>
        {
            var intervals = new List<(int Start, int End)>();
            foreach (var line in group.OrderBy(v => v.Start).ThenBy(v => v.End))
            {
                if (intervals.Count == 0 || line.Start > intervals[^1].End + tolerance)
                    intervals.Add((line.Start, line.End));
                else intervals[^1] = (intervals[^1].Start, Math.Max(intervals[^1].End, line.End));
            }
            return new WallCluster(Math.Round(Median(group.Select(v => v.Coordinate))), intervals,
                intervals.Sum(v => v.End - v.Start));
        }).ToList();
    }

    private (int Start, int End, double Consistency) FloorColorSpan(bool horizontal,
        int crossStart, int crossEnd, int start, int end, double icon, double size)
    {
        var spanLimit = horizontal ? Floor.Width : Floor.Height;
        var crossLimit = horizontal ? Floor.Height : Floor.Width;
        start = Math.Clamp(start, 0, spanLimit - 1);
        end = Math.Clamp(end, start + 1, spanLimit);
        crossStart = Math.Clamp(crossStart + 2, 0, crossLimit - 1);
        crossEnd = Math.Clamp(crossEnd - 2, crossStart + 1, crossLimit);
        var values = new float[end - start];
        var known = new bool[values.Length];
        PrepareExitColorIntegrals();
        for (var p = start; p < end; p++)
        {
            var valid = ExitColorSum(_visibleIntegral!, horizontal, p, crossStart, crossEnd);
            var brown = ExitColorSum(_brownIntegral!, horizontal, p, crossStart, crossEnd);
            known[p - start] = valid / (double)(crossEnd - crossStart) >= .2;
            values[p - start] = valid == 0 ? 0 : brown / (float)valid;
        }
        using var row = new Mat(1, values.Length, MatType.CV_32F);
        for (var i = 0; i < values.Length; i++) row.Set(0, i, values[i]);
        using var smoothed = new Mat();
        Cv2.Blur(row, smoothed, new Size(Math.Max(5, (int)Math.Round(size * .35)) | 1, 1));
        for (var i = 0; i < values.Length; i++) values[i] = smoothed.At<float>(0, i);
        var inward = Math.Max((int)Math.Round(size * 1.5), (int)Math.Round((crossEnd - crossStart) * .65));
        var probe = Math.Clamp((int)Math.Round(icon) + ((icon - start) / (end - start) <= .5 ? inward : -inward), start, end - 1) - start;
        var target = values[probe] >= .43;
        var runLength = Math.Max(5, (int)Math.Round(size * .35));
        int Scan(int step)
        {
            var mismatch = 0;
            for (var cursor = probe; cursor >= 0 && cursor < values.Length; cursor += step)
            {
                mismatch = !known[cursor] || (values[cursor] >= .43) == target ? 0 : mismatch + 1;
                if (mismatch >= runLength) return step < 0 ? cursor + runLength : cursor - runLength + 1;
            }
            return step < 0 ? 0 : values.Length;
        }
        var first = Math.Clamp(Scan(-1), 0, probe);
        var last = Math.Clamp(Scan(1), probe + 1, values.Length);
        return (start + first, start + last,
            values.Skip(first).Take(last - first).Count(v => (v >= .43) == target) / (double)(last - first));
    }
}
