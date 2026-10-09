using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    // Same raw-pixel preparation as the measured private entry-domain path.
    // These are local positive-wall witnesses, not semantic ports or identity.
    private static IReadOnlyList<Point> BuildAutomaticEntryWallWitnesses(
        Vpsg3LiveObservation observation, AutomaticGeometryInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edges = observation.ProposalEdges;
        if (observation.AutomaticWallEvidence is not { } evidence) return [];
        var width = edges.Width;
        var height = edges.Height;
        var viewport = observation.ViewportBounds;
        var gates = input.Gates;
        var controls = input.GateDetection.Gates.Select(gate => gate.ScreenBounds).ToArray();
        var pixels = new HashSet<Point>();
        foreach (var corner in input.Corners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0) return [];
            // Short corner arms remain useful pose proposals. They cannot let
            // a small decorative notch lend its longer arm hard exclusion power.
            if (!(corner.RayALength >= 20 && corner.RayBLength >= 20)) continue;
            foreach (var ray in new[]
            {
                (Length: corner.RayALength, Direction: corner.RayA),
                (Length: corner.RayBLength, Direction: corner.RayB)
            })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ray.Length < 20) continue;
                for (var distance = 4d; distance <= ray.Length - 4; distance += 8)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0) return [];
                    var ideal = corner.Point + ray.Direction * distance;
                    Point? nearest = null;
                    var error = double.PositiveInfinity;
                    for (var y = Math.Max(0, (int)Math.Floor(ideal.Y) - 3);
                        y <= Math.Min(height - 1, (int)Math.Ceiling(ideal.Y) + 3); y++)
                    for (var x = Math.Max(0, (int)Math.Floor(ideal.X) - 3);
                        x <= Math.Min(width - 1, (int)Math.Ceiling(ideal.X) + 3); x++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (edges.At<byte>(y, x) != 255 || evidence.At<byte>(y, x) != 255 || controls.Any(bounds =>
                            x + viewport.X >= bounds.X - 5
                            && x + viewport.X <= bounds.X + bounds.Width + 5
                            && y + viewport.Y >= bounds.Y - 5
                            && y + viewport.Y <= bounds.Y + bounds.Height + 5)) continue;
                        var delta = double.Hypot(x - ideal.X, y - ideal.Y);
                        if (delta <= 3 && delta < error)
                        {
                            nearest = new(x, y);
                            error = delta;
                        }
                    }
                    if (nearest is { } actual) pixels.Add(actual);
                }
            }
        }

        // Stable Y/X ordering supplies the same tie break as the private probe.
        // Greedy geometric coverage has no map, pose or scale input.
        var pool = pixels.OrderBy(pixel => pixel.Y).ThenBy(pixel => pixel.X).ToArray();
        var used = new bool[pool.Length];
        var nearestDistances = Enumerable.Repeat(double.PositiveInfinity, pool.Length).ToArray();
        var witnesses = new List<Point>();
        while (witnesses.Count < Math.Min(64, pool.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0) return [];
            var selectedIndex = -1;
            var greatestDistance = double.NegativeInfinity;
            for (var i = 0; i < pool.Length; i++)
            {
                if (used[i]) continue;
                var distance = witnesses.Count == 0
                    ? gates.Count == 0 ? 0 : gates.Min(gate => double.Hypot(pool[i].X - gate.X, pool[i].Y - gate.Y))
                    : nearestDistances[i];
                // Strict comparison retains the original stable Y/X tie break.
                if (distance > greatestDistance)
                {
                    greatestDistance = distance;
                    selectedIndex = i;
                }
            }
            var selected = pool[selectedIndex];
            witnesses.Add(selected);
            used[selectedIndex] = true;
            for (var i = 0; i < pool.Length; i++)
            {
                if (used[i]) continue;
                nearestDistances[i] = Math.Min(nearestDistances[i],
                    double.Hypot(pool[i].X - selected.X, pool[i].Y - selected.Y));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0) return [];
        return witnesses;
    }
}
