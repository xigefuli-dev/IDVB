namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    // Sparse cone scores retrieve scale basins; they do not optimize the dense
    // verifier's support boundary. Refine a near fit before excluding its identity.
    // Every proposal remains gate-anchored and must pass the unchanged full verifier.
    internal static SideEntranceScanCandidate? RefineIdentityPose(SideEntranceScanCandidate seed,
        ScanFrameEvidence frame, MapScreenRect viewport, ScanExecutionContext context)
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
        var proposals = new List<(double scale, double x, double y, int hits, double distance)>();
        for (var step = -15; step <= 15; step++)
        {
            if (!context.CanCompute || context.RemainingMilliseconds <= 150) return null;
            var scale = seed.MatchScale * (1 + step * .001);
            if (scale < context.Policy.MinimumScale || scale > context.Policy.MaximumScale) continue;
            var bestHits = -1;
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
                foreach (var p in points)
                {
                    var d = index.Distance((p.X - x) / scale, (p.Y - y) / scale, scale);
                    if (d <= ScanIdentityVerifier.SupportTolerancePixels) hits++;
                    distance += d;
                }
                context.TestedHypotheses++;
                if (hits > bestHits || (hits == bestHits && distance < bestDistance))
                { bestHits = hits; bestDistance = distance; bestX = x; bestY = y; }
            }
            proposals.Add((scale, bestX, bestY, bestHits, bestDistance));
        }
        foreach (var p in proposals.OrderByDescending(p => p.hits).ThenBy(p => p.distance))
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
