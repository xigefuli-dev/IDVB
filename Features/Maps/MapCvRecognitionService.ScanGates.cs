using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private GateDetectionResult DetectScanGates(Mat image, MapScreenRect viewport, double clientWidth,
        double threshold, GateSearchContext search)
    {
        if (ScanExecutionContext.Current?.Policy.Mode != ScanPerformanceMode.Fast)
            return _gateDetector.Detect(image, viewport, clientWidth, threshold, search);

        // Search the complete view and scale band at half resolution, then confirm every
        // proposed spatial gate at source resolution. Identity still sees the full frame.
        var timer = Stopwatch.StartNew();
        using var small = new Mat();
        Cv2.Resize(image, small, new Size((image.Width + 1) / 2, (image.Height + 1) / 2),
            interpolation: InterpolationFlags.Area);
        var coarse = _gateDetector.Detect(small, viewport, clientWidth, Math.Min(threshold, .55), search, 2);
        var confirmed = new List<GateDetection>();
        var calls = coarse.MatchTemplateCalls;
        var scales = coarse.ScalesEvaluated;
        var complete = !coarse.BudgetExceeded;
        foreach (var gate in coarse.Gates)
        {
            var left = ScanExecutionContext.Current!.RemainingMilliseconds - 60;
            if (left <= 0) { complete = false; break; }
            var exact = _gateDetector.Detect(image, viewport, clientWidth, threshold,
                new GateSearchContext
                {
                    Mode = GateSearchMode.LocalConfirmationSearch,
                    PredictedGateRegions = [gate.ScreenBounds], PredictedScale = gate.Scale,
                    TimeBudgetMilliseconds = left, AllowDualGateEarlyExit = false,
                    LocalRoiMinimumPaddingPixels = 8
                });
            calls += exact.MatchTemplateCalls;
            scales += exact.ScalesEvaluated;
            complete &= !exact.BudgetExceeded;
            confirmed.AddRange(exact.Gates);
        }
        return new GateDetectionResult
        {
            Gates = GateTemplateDetector.ClusterAcrossScales(confirmed)
                .Select(group => group.OrderByDescending(g => g.Score).First()).ToArray(),
            RawCandidates = confirmed, SearchModeUsed = GateSearchMode.FullSearch,
            StopReason = complete ? GateSearchStopReason.Completed : GateSearchStopReason.BudgetExceeded,
            BudgetExceeded = !complete, MatchTemplateCalls = calls, ScalesEvaluated = scales,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
        };
    }

}
