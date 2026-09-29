namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    // Sparse cone scores retrieve scale basins; they do not optimize the dense
    // verifier's support boundary. Refine a near fit before excluding its identity.
    // Every proposal remains gate-anchored and must pass the unchanged full verifier.
    internal static SideEntranceScanCandidate? RefineIdentityPose(SideEntranceScanCandidate seed,
        ScanFrameEvidence frame, MapScreenRect viewport, ScanExecutionContext context,
        double maximumCompetitiveFitCost = double.PositiveInfinity)
    {
        var profile = MapFloorRules.GetFloorProfile(seed.Map, seed.FloorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(seed.Map, seed.FloorKey);
        if (seed.StructureIndex is not { } index || seed.AssociatedGate is not { } gate
            || profile is null || anchor?.Bounds?.IsValid != true) return null;
        var points = ScanFrameEvidence.SampleUniform(frame.DensePoints, frame.Source.Width, frame.Source.Height, 1024);
        var ax = (anchor.Bounds.X + anchor.Bounds.Width / 2) * profile.RecognitionPixelWidth;
        var ay = (anchor.Bounds.Y + anchor.Bounds.Height / 2) * profile.RecognitionPixelHeight;
        var gx = gate.ScreenBounds.CenterX - viewport.X;
        var gy = gate.ScreenBounds.CenterY - viewport.Y;
        var proposals = new List<(double scale, double x, double y, double support, double distance)>();
        for (var step = -15; step <= 15; step++)
        {
            if (!context.CanCompute || context.RemainingMilliseconds <= 150) return null;
            var scale = seed.MatchScale * (1 + step * .001);
            if (scale < context.Policy.MinimumScale || scale > context.Policy.MaximumScale) continue;
            var bestSupport = -1d;
            var bestDistance = double.PositiveInfinity;
            var bestX = 0d;
            var bestY = 0d;
            for (var dx = -3; dx <= 3; dx++)
            for (var dy = -3; dy <= 3; dy++)
            {
                var x = gx + dx - ax * scale;
                var y = gy + dy - ay * scale;
                var hits = 0;
                var distance = 0d;
                var tested = 0;
                var dominated = false;
                foreach (var p in points)
                {
                    var rx = (p.X - x) / scale;
                    var ry = (p.Y - y) / scale;
                    if (index.IsUnknown(rx, ry)) continue;
                    var d = index.Distance(rx, ry, scale);
                    if (d <= ScanIdentityVerifier.SupportTolerancePixels) hits++;
                    distance += d;
                    tested++;
                    // This is a lower bound on the FULL dense-frame fit cost:
                    // every untested point is assumed to match perfectly. The
                    // uniform sample is a subset, so the bound also holds when
                    // dense points exceed the sample size. No near tie is pruned.
                    if (ScanIdentityVerifier.CannotBeatFitCost(distance, tested - hits,
                        frame.DensePoints.Length, maximumCompetitiveFitCost))
                    {
                        dominated = true;
                        break;
                    }
                }
                context.TestedHypotheses++;
                if (dominated || !ScanStructureIndex.HasEnoughKnownPoints(tested, points.Length, 80)) continue;
                var support = hits / (double)tested;
                var mean = distance / tested;
                if (support > bestSupport || (support == bestSupport && mean < bestDistance))
                { bestSupport = support; bestDistance = mean; bestX = x; bestY = y; }
            }
            if (bestSupport >= 0) proposals.Add((scale, bestX, bestY, bestSupport, bestDistance));
        }
        foreach (var p in proposals.OrderByDescending(p => p.support).ThenBy(p => p.distance))
        {
            if (!context.CanCompute) return null;
            var transform = new MapOverlayTransform { ScaleX = p.scale, ScaleY = p.scale,
                OffsetX = viewport.X + p.x, OffsetY = viewport.Y + p.y };
            var evidence = ScanIdentityVerifier.Verify(frame, index, transform, viewport, context);
            if (evidence.State != ScanIdentityState.Supported) continue;
            return new SideEntranceScanCandidate
            {
                Map = seed.Map, FloorKey = seed.FloorKey, MatchScale = p.scale, MatchScore = seed.MatchScore,
                MatchLocation = new(p.x, p.y, index.Width * p.scale, index.Height * p.scale),
                ReferenceCenterX = index.Width / 2d, ReferenceCenterY = index.Height / 2d,
                StructureIndex = index, AssociatedGate = gate, AssociatedGateIndex = seed.AssociatedGateIndex,
                GateSpatialResidualPixels = Math.Sqrt(Math.Pow(p.x + ax * p.scale - gx, 2)
                    + Math.Pow(p.y + ay * p.scale - gy, 2)),
                GateAssociationKind = seed.GateAssociationKind, IdentityEvidence = evidence,
                Disposition = SideEntranceCandidateDisposition.NeedsVerification
            };
        }
        return null;
    }
}
