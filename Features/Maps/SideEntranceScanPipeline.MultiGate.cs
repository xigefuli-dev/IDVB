using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    public IReadOnlyList<SideEntranceScanCandidate> RunScan(Mat capturedFrame,
        IReadOnlyList<(MapRecord map, string floorKey, Mat featureTemplate)> candidates,
        IReadOnlyList<GateDetection> detectedGates, int topK = 5,
        MapScreenRect? viewportBounds = null, Action<double>? progress = null)
    {
        if (capturedFrame.Empty() || candidates.Count == 0 || detectedGates.Count == 0) return [];
        var context = ScanExecutionContext.Current;
        if (context is { CanCompute: false })
        {
            context.RetrievalCompleted = false;
            return [];
        }
        var policy = context?.Policy ?? ScanExecutionPolicy.For(ScanPerformanceMode.Balanced);
        var viewport = viewportBounds ?? new MapScreenRect(0, 0, capturedFrame.Width, capturedFrame.Height);
        // Shared evidence depends only on this capture. An unrelated candidate's
        // authored anchor or maximum search scale must never erase visible walls.
        var frame = new ScanFrameEvidence(capturedFrame, viewport, detectedGates, policy);
        if (context is not null)
        {
            context.SetFrame(frame);
            context.EligibleIdentities = candidates.Select(c => (c.map.Id, c.floorKey)).Distinct().Count();
        }
        try
        {
            if (frame.SearchPoints.Length == 0) return [];
            var found = new SideEntranceScanCandidate?[candidates.Count];
            var completed = 0;
            Parallel.For(0, candidates.Count, new ParallelOptions
            { MaxDegreeOfParallelism = Math.Max(1, SideEntranceScanRules.ScanParallelism) }, i =>
            {
                if (context is { CanCompute: false }) { context.RetrievalCompleted = false; return; }
                var (map, floor, line) = candidates[i];
                var alternatives = new List<SideEntranceScanCandidate>();
                for (var g = 0; g < detectedGates.Count; g++)
                {
                    if (!detectedGates[g].ScreenBounds.IsValid) continue;
                    alternatives.AddRange(SearchFloor(map, floor, line, frame, detectedGates[g], g, viewport, policy, context));
                }
                var best = alternatives.OrderByDescending(c => c.MatchScore).FirstOrDefault();
                if (best is not null)
                {
                    found[i] = best.WithHypotheses(alternatives.OrderByDescending(c => c.MatchScore).ToArray());
                }
                var finished = Interlocked.Increment(ref completed);
                progress?.Invoke(finished / (double)candidates.Count);
            });
            var results = found.Where(c => c is not null).Select(c => c!).OrderByDescending(c => c.MatchScore).ToArray();
            if (context is not null && (completed != candidates.Count || results.Length != context.EligibleIdentities
                || topK < results.Length))
                context.RetrievalCompleted = false;
            for (var i = 0; i < results.Length; i++)
                results[i].TemplateMargin = i + 1 < results.Length ? results[i].MatchScore - results[i + 1].MatchScore : 0;
            return results.Take(Math.Max(1, topK)).ToArray();
        }
        finally { if (context is null) frame.Dispose(); }
    }

    internal static void MaskDetectedGates(Mat frame, IReadOnlyList<GateDetection> gates, MapScreenRect? viewport)
    {
        if (viewport is not { IsValid: true } bounds) return;
        foreach (var gate in gates)
        {
            var rect = new Rect((int)(gate.ScreenBounds.X - bounds.X), (int)(gate.ScreenBounds.Y - bounds.Y),
                (int)gate.ScreenBounds.Width, (int)gate.ScreenBounds.Height).Intersect(new Rect(0, 0, frame.Width, frame.Height));
            if (rect.Width > 0 && rect.Height > 0) Cv2.Rectangle(frame, rect, Scalar.Black, -1);
        }
    }
}
