using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    private readonly record struct SearchPeak(double Scale, double X, double Y, double Score);

    internal static IReadOnlyList<SideEntranceScanCandidate> RefineVariant(
        SideEntranceScanCandidate candidate, ScanFrameEvidence frame, MapScreenRect viewport,
        ScanExecutionContext context)
    {
        var index = candidate.StructureIndex;
        var profile = MapFloorRules.GetFloorProfile(candidate.Map, candidate.FloorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(candidate.Map, candidate.FloorKey);
        if (index is null || profile is null || anchor?.Bounds?.IsValid != true) return [];
        var points = ScanFrameEvidence.SampleUniform(frame.DensePoints,
            frame.Observation.ObservedEdges.Width, frame.Observation.ObservedEdges.Height, 1024);
        var refined = new List<SideEntranceScanCandidate>();
        // Refinement is optional. Leave room for three variant registrations and
        // final evidence/commit instead of exhausting the scan's shared deadline.
        const int verificationReserve = 400;
        foreach (var seed in candidate.SearchHypotheses)
        {
            if (seed.AssociatedGate is not { } gate) continue;
            var ax = (anchor.Bounds.X + anchor.Bounds.Width / 2) * profile.RecognitionPixelWidth;
            var ay = (anchor.Bounds.Y + anchor.Bounds.Height / 2) * profile.RecognitionPixelHeight;
            var gx = gate.ScreenBounds.CenterX - viewport.X;
            var gy = gate.ScreenBounds.CenterY - viewport.Y;
            var best = new SearchPeak(seed.MatchScale, seed.MatchLocation.X, seed.MatchLocation.Y,
                index.Score(points, seed.MatchScale, seed.MatchLocation.X, seed.MatchLocation.Y));
            for (var step = -4; step <= 4; step++)
            {
                if (!context.CanCompute || context.RemainingMilliseconds <= verificationReserve)
                    return RetainProposals();
                var scale = seed.MatchScale * (1 + step * .0025);
                if (scale < context.Policy.MinimumScale || scale > context.Policy.MaximumScale) continue;
                for (var dx = -3; dx <= 3; dx++)
                for (var dy = -3; dy <= 3; dy++)
                {
                    var x = gx + dx - ax * scale;
                    var y = gy + dy - ay * scale;
                    var score = index.Score(points, scale, x, y, best.Score);
                    context.TestedHypotheses++;
                    if (score > best.Score) best = new(scale, x, y, score);
                }
            }
            refined.Add(new SideEntranceScanCandidate
            {
                Map = seed.Map, FloorKey = seed.FloorKey, MatchScale = best.Scale, MatchScore = best.Score,
                MatchLocation = new(best.X, best.Y, index.Width * best.Scale, index.Height * best.Scale),
                ReferenceCenterX = index.Width / 2d, ReferenceCenterY = index.Height / 2d,
                StructureIndex = index, AssociatedGate = gate, AssociatedGateIndex = seed.AssociatedGateIndex,
                GateAssociationKind = SideEntranceGateAssociationKind.DetectedGate
            });
        }
        return RetainProposals();

        IReadOnlyList<SideEntranceScanCandidate> RetainProposals()
        {
            if (refined.Count > 0) context.VariantRefinementCount++;
            // Dense-point refinement is a proposal, not permission to discard a seed
            // that the original search already found. Its different sampling objective
            // can improve its own score while worsening the full-frame verifier.
            return candidate.SearchHypotheses.Concat(refined)
                .DistinctBy(c => (c.MatchScale, c.MatchLocation.X, c.MatchLocation.Y, c.AssociatedGateIndex))
                .ToArray();
        }
    }

    private static IReadOnlyList<SideEntranceScanCandidate> SearchFloor(
        MapRecord map, string floorKey, Mat line, ScanFrameEvidence frame,
        GateDetection gate, int gateIndex, MapScreenRect viewport, ScanExecutionPolicy policy,
        ScanExecutionContext? context)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey);
        if (profile is null || anchor?.Bounds?.IsValid != true || line.Empty()) return [];
        var index = ScanStructureIndex.Get(line).WithScanAnchor(map, floorKey);
        var ax = (anchor.Bounds.X + anchor.Bounds.Width / 2) * profile.RecognitionPixelWidth;
        var ay = (anchor.Bounds.Y + anchor.Bounds.Height / 2) * profile.RecognitionPixelHeight;
        var gx = gate.ScreenBounds.CenterX - viewport.X;
        var gy = gate.ScreenBounds.CenterY - viewport.Y;
        // Every mode starts with the same points and the same completed fast search.
        // Additional work retains those winners instead of replacing the sampling
        // objective and losing a basin that a cheaper mode already found.
        var retained = new List<SearchPeak>();
        foreach (var mode in new[] { ScanPerformanceMode.Fast, ScanPerformanceMode.Balanced, ScanPerformanceMode.Quality })
        {
            if ((int)mode > (int)policy.Mode) break;
            var stage = ScanExecutionPolicy.For(mode) with
            { MinimumScale = policy.MinimumScale, MaximumScale = policy.MaximumScale };
            retained.AddRange(SearchPeaks(index, frame.SearchPoints, ax, ay, gx, gy, stage, context));
        }
        var unique = new List<SearchPeak>();
        foreach (var peak in retained.OrderByDescending(p => p.Score))
        {
            if (unique.Any(p => Math.Abs(Math.Log(p.Scale / peak.Scale)) < policy.FineScaleStep)) continue;
            unique.Add(peak);
            if (unique.Count == policy.BasinCount) break;
        }
        return unique.Select(p =>
        {
            var candidate = new SideEntranceScanCandidate
            {
                Map = map, FloorKey = floorKey, MatchScale = p.Scale, MatchScore = p.Score,
                MatchLocation = new MapScreenRect(p.X, p.Y, line.Width * p.Scale, line.Height * p.Scale),
                ReferenceCenterX = line.Width / 2d, ReferenceCenterY = line.Height / 2d,
                StructureIndex = index, AssociatedGate = gate, AssociatedGateIndex = gateIndex,
                GateAssociationKind = SideEntranceGateAssociationKind.DetectedGate,
                Disposition = SideEntranceCandidateDisposition.NeedsVerification
            };
            candidate.GateSpatialResidualPixels = CalculateGateResidual(candidate, gate, viewport);
            return candidate;
        }).ToArray();
    }

    private static IReadOnlyList<SearchPeak> SearchPeaks(ScanStructureIndex index, Point[] points,
        double ax, double ay, double gx, double gy, ScanExecutionPolicy policy, ScanExecutionContext? context)
    {
        var min = policy.MinimumScale;
        var max = policy.MaximumScale;
        var scales = new SortedSet<double> { min, max, Math.Clamp(1d, min, max) };
        for (var s = min; s <= max; s *= 1 + policy.CoarseScaleStep) scales.Add(s);
        var peaks = new List<SearchPeak>();
        void Add(SearchPeak peak)
        {
            var duplicate = peaks.FindIndex(p => Math.Abs(Math.Log(p.Scale / peak.Scale)) < policy.CoarseScaleStep * 2);
            if (duplicate >= 0)
            {
                if (peaks[duplicate].Score >= peak.Score) return;
                peaks.RemoveAt(duplicate);
            }
            peaks.Add(peak);
            peaks.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (peaks.Count > policy.BasinCount) peaks.RemoveAt(peaks.Count - 1);
        }
        foreach (var scale in scales)
        {
            if (context is { CanCompute: false }) { context.RetrievalCompleted = false; break; }
            for (var dx = -3; dx <= 3; dx += policy.ResidualStep)
            for (var dy = -3; dy <= 3; dy += policy.ResidualStep)
            {
                var x = gx + dx - ax * scale;
                var y = gy + dy - ay * scale;
                var competitiveScore = peaks.Count == policy.BasinCount ? peaks[^1].Score : 0;
                Add(new(scale, x, y, index.Score(points, scale, x, y, competitiveScore)));
                if (context is not null) Interlocked.Increment(ref context.TestedHypotheses);
            }
        }
        // Freeze each coarse basin: updating a winner must not move its search grid.
        var refined = new List<SearchPeak>();
        foreach (var seed in peaks.ToArray())
        {
            var best = seed;
            var steps = (int)Math.Ceiling(policy.CoarseScaleStep / policy.FineScaleStep);
            for (var i = -steps; i <= steps; i++)
            {
                if (context is { CanCompute: false }) { context.RetrievalCompleted = false; break; }
                var scale = seed.Scale * (1 + i * policy.FineScaleStep);
                if (scale < min || scale > max) continue;
                for (var dx = -3; dx <= 3; dx++)
                for (var dy = -3; dy <= 3; dy++)
                {
                    var x = gx + dx - ax * scale;
                    var y = gy + dy - ay * scale;
                    var score = index.Score(points, scale, x, y, best.Score);
                    if (score > best.Score) best = new(scale, x, y, score);
                    if (context is not null) Interlocked.Increment(ref context.TestedHypotheses);
                }
            }
            refined.Add(best);
        }
        return refined;
    }
}
