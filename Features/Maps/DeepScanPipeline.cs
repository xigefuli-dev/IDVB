using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Door-independent local geometric retrieval. Produces proposals only;
/// identity verification, mandatory alignment and publication remain separate gates.</summary>
internal static class DeepScanPipeline
{
    private readonly record struct Pose(double Scale, double X, double Y, double Score);

    public static IReadOnlyList<SideEntranceScanCandidate> Run(Mat image,
        IReadOnlyList<(MapRecord map, string floorKey, Mat template)> inputs,
        MapScreenRect viewport, ScanExecutionContext context, Action<double>? progress = null,
        IReadOnlyList<GateDetection>? gates = null)
    {
        if (context.Policy.Mode != ScanPerformanceMode.DeepScan)
            throw new InvalidOperationException("Local geometry retrieval is exclusive to DeepScan.");
        context.EligibleIdentities = inputs.Count;
        if (!context.CanCompute || image.Empty()) { context.RetrievalCompleted = false; return []; }
        // DeepScan has no gate-anchored pose. Only the detected icon's measured
        // screen bounds are known here. Expanding an unrelated catalog anchor
        // by MaximumScale can erase the entire distinctive visible corridor.
        // Keep one measured exclusion for all identities; reference anchors
        // remain masked in the prebuilt lines and cannot supply positive evidence.
        var frame = new ScanFrameEvidence(image, viewport, gates ?? [], context.Policy);
        context.SetFrame(frame);
        var corners = DeepScanStructureIndex.SelectQueryCorners(frame.Contours, image.Width, image.Height);
        if (frame.DensePoints.Length < 80 || corners.Length < 2)
        { context.RetrievalCompleted = false; return []; }
        var coarsePoints = ScanFrameEvidence.SampleUniform(frame.DensePoints, image.Width, image.Height, 64);
        const int proposalPool = 32;
        var found = new List<SideEntranceScanCandidate>(inputs.Count);
        foreach (var (map, floor, line) in inputs)
        {
            if (!context.CanCompute || context.RemainingMilliseconds <= 500)
            { context.RetrievalCompleted = false; break; }
            if (!DeepScanStructureIndex.TryGet(line, out var index) || index is null || !index.HasGeometry)
            { context.RetrievalCompleted = false; found.Add(Missing(map, floor, "deepscan-index-unavailable")); continue; }
            var distances = index.Distances.WithScanAnchor(map, floor);
            var peaks = new List<Pose>();
            var visited = new HashSet<(int, int, int)>();
            var complete = true;
            var insufficientKnownEvidence = false;
            foreach (var live in corners)
            {
                foreach (var reference in index.Lookup(live))
                {
                    if (!context.CanCompute || context.RemainingMilliseconds <= 500)
                    { complete = false; break; }
                    if (DeepScanStructureIndex.TryPose(reference, live, context.Policy, out var scale, out var x, out var y))
                        AddPose(scale, x, y);
                }
                if (!complete) break;
            }
            // Match the distance between bends, not fog-truncated wall lengths.
            // Direction lookup is prewarmed; score only geometrically consistent
            // pairs against the same sparse evidence and bounded proposal pool.
            var landmarks = complete && (peaks.Count == 0 || peaks[0].Score < .98)
                ? corners.DistinctBy(c => c.B).Take(6).ToArray() : [];
            var paired = landmarks.Select(live => (Live: live, References: index.LookupPartial(live).ToArray())).ToArray();
            for (var a = 0; a < paired.Length && complete; a++)
            for (var b = a + 1; b < paired.Length && complete; b++)
            {
                var first = paired[a];
                var second = paired[b];
                if (first.Live.B.DistanceTo(second.Live.B) < 30) continue;
                foreach (var reference in first.References)
                {
                    if (!context.CanCompute || context.RemainingMilliseconds <= 500) { complete = false; break; }
                    foreach (var other in second.References)
                        if (DeepScanStructureIndex.TryCornerPairPose(reference, other, first.Live, second.Live,
                            context.Policy, out var scale, out var x, out var y)) AddPose(scale, x, y);
                }
            }
            // Broader length-independent lookup is needed for fog-clipped walls.
            // Bound it to the strongest local landmarks; intact high-fit poses
            // already have both arms measured and need no extra retrieval.
            if (complete && (peaks.Count == 0 || peaks[0].Score < .97))
                foreach (var live in corners.Take(8))
                {
                    foreach (var reference in index.LookupPartial(live))
                    {
                        if (!context.CanCompute || context.RemainingMilliseconds <= 500)
                        { complete = false; break; }
                        if (DeepScanStructureIndex.TryPartialPose(reference, live, context.Policy, out var scale, out var x, out var y))
                            AddPose(scale, x, y);
                    }
                    if (!complete) break;
                }
            void AddPose(double scale, double x, double y)
            {
                context.TestedHypotheses++;
                if (!visited.Add(((int)Math.Round(Math.Log(scale) / .003), (int)Math.Round(x / 2), (int)Math.Round(y / 2)))) return;
                var score = Score(distances, coarsePoints, scale, x, y,
                    peaks.Count == proposalPool ? peaks[^1].Score : double.NegativeInfinity);
                if (double.IsNaN(score)) insufficientKnownEvidence = true;
                if (!double.IsFinite(score)) return;
                peaks.Add(new(scale, x, y, score));
                peaks.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (peaks.Count > proposalPool) peaks.RemoveAt(peaks.Count - 1);
            }
            if (!complete) context.RetrievalCompleted = false;
            peaks = peaks.Select(p =>
                {
                    var score = Score(distances, frame.SearchPoints, p.Scale, p.X, p.Y);
                    if (double.IsNaN(score)) insufficientKnownEvidence = true;
                    return p with { Score = score };
                })
                .Where(p => double.IsFinite(p.Score))
                .OrderByDescending(p => p.Score).Take(context.Policy.BasinCount).ToList();
            // Refine around the observed patch, not the reference origin: a tiny
            // scale correction must not push a far-from-origin patch sideways.
            var pivotX = frame.SearchPoints.Average(p => p.X);
            var pivotY = frame.SearchPoints.Average(p => p.Y);
            for (var i = 0; i < Math.Min(2, peaks.Count) && complete; i++)
            {
                var best = peaks[i];
                foreach (var step in new[] { .005, .001 })
                {
                    var seed = best;
                    // Test the least-fitting evidence first so losing local steps
                    // hit the same exact distance bound early. No points are
                    // removed; final ranking uses the original sample order.
                    var orderedPoints = frame.SearchPoints.OrderByDescending(p =>
                        distances.DeepScanDistance((p.X - seed.X) / seed.Scale,
                            (p.Y - seed.Y) / seed.Scale, seed.Scale)).ToArray();
                    best = best with { Score = Score(distances, orderedPoints, best.Scale, best.X, best.Y) };
                    for (var ds = -2; ds <= 2; ds++)
                    for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        if (!context.CanCompute || context.RemainingMilliseconds <= 500) { complete = false; break; }
                        var scale = seed.Scale * (1 + ds * step);
                        if (scale < context.Policy.MinimumScale || scale > context.Policy.MaximumScale) continue;
                        var x = pivotX - (pivotX - seed.X) * scale / seed.Scale + dx;
                        var y = pivotY - (pivotY - seed.Y) * scale / seed.Scale + dy;
                        var score = Score(distances, orderedPoints, scale, x, y, best.Score);
                        context.TestedHypotheses++;
                        if (score > best.Score) best = new(scale, x, y, score);
                    }
                }
                var fullScore = Score(distances, frame.SearchPoints, best.Scale, best.X, best.Y);
                if (fullScore > peaks[i].Score) peaks[i] = best with { Score = fullScore };
            }
            if (!complete) context.RetrievalCompleted = false;
            peaks.Sort((a, b) => b.Score.CompareTo(a.Score));
            var proposals = peaks.Select(p => new SideEntranceScanCandidate
            {
                Map = map, FloorKey = floor, StructureIndex = distances,
                MatchScale = p.Scale, MatchScore = p.Score,
                MatchLocation = new(p.X, p.Y, index.Width * p.Scale, index.Height * p.Scale),
                ReferenceCenterX = index.Width / 2d, ReferenceCenterY = index.Height / 2d
            }).ToArray();
            if (proposals.Length > 0) found.Add(proposals[0].WithHypotheses(proposals));
            else
            {
                var missing = Missing(map, floor, insufficientKnownEvidence
                    ? "deepscan-insufficient-unmasked-reference-structure" : "deepscan-local-geometry-not-found");
                // Only a completed comparison may reject local geometry. An interrupted
                // lookup remains unverified and blocks automatic identity selection.
                if (complete && !insufficientKnownEvidence)
                    missing.IdentityEvidence = new(ScanIdentityState.Excluded, 0, frame.DensePoints.Length,
                        50, 0, 0, "deepscan-local-geometry-not-found");
                found.Add(missing);
            }
            progress?.Invoke(found.Count / (double)inputs.Count);
        }
        return found.OrderByDescending(c => c.MatchScore).ToArray();
    }

    private static SideEntranceScanCandidate Missing(MapRecord map, string floor, string reason) => new()
    { Map = map, FloorKey = floor, IdentityEvidence = ScanIdentityEvidence.Unverified(reason) };

    private static double Score(ScanStructureIndex index, Point[] points, double scale, double x, double y,
        double minimumScore = double.NegativeInfinity)
    {
        var sum = 0d;
        var known = 0;
        var inverseScale = 1d / scale;
        var maximumDistance = (1 - minimumScore) * points.Length * 10;
        foreach (var p in points)
        {
            var rx = (p.X - x) * inverseScale;
            var ry = (p.Y - y) * inverseScale;
            if (index.IsUnknownWithinSupport(rx, ry, scale)) continue;
            known++;
            sum += Math.Min(10, index.DeepScanDistance(rx, ry, scale));
            // Exact lower bound: remaining distances cannot be negative. Pruning
            // cannot discard a pose that would outrank the current retained basin.
            if (sum > maximumDistance) return double.NegativeInfinity;
        }
        return ScanStructureIndex.HasEnoughKnownPoints(known, points.Length, 16)
            ? 1 - sum / (known * 10) : double.NaN;
    }
}
