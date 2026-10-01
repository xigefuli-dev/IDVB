using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private GateDetectionResult DetectScanGates(Mat image, MapScreenRect viewport, double clientWidth,
        double threshold, GateSearchContext search, Mat? colorImage = null)
    {
        if (ScanExecutionContext.Current?.Policy.Mode != ScanPerformanceMode.Fast
            && Math.Max(image.Width, image.Height) <= 480)
            return AddOcclusionRecovery(_gateDetector.Detect(image, viewport, clientWidth, threshold, search),
                colorImage, viewport, clientWidth, search);

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
        var confirmations = new List<object>();
        var scaleEvidence = coarse.ScaleEvidence.ToList();
        foreach (var gate in coarse.Gates)
        {
            var left = ScanExecutionContext.Current is { IsAutomatic: true } execution
                ? execution.RemainingMilliseconds - 60 : int.MaxValue;
            if (left <= 0) { complete = false; break; }
            var exact = _gateDetector.Detect(image, viewport, clientWidth, threshold,
                new GateSearchContext
                {
                    Mode = GateSearchMode.LocalConfirmationSearch,
                    PredictedGateRegions = [gate.ScreenBounds], PredictedScale = gate.Scale,
                    TimeBudgetMilliseconds = left, AllowDualGateEarlyExit = false,
                    CancellationToken = search.CancellationToken,
                    LocalRoiMinimumPaddingPixels = 8
                });
            calls += exact.MatchTemplateCalls;
            scales += exact.ScalesEvaluated;
            complete &= !exact.BudgetExceeded;
            complete &= exact.StopReason != GateSearchStopReason.Canceled;
            scaleEvidence.AddRange(exact.ScaleEvidence);
            confirmed.AddRange(exact.Gates);
            confirmations.Add(new { gate.Score, gate.Scale, gate.ScreenBounds,
                confirmedCount = exact.Gates.Count, exact.BudgetExceeded, exact.ElapsedMilliseconds,
                stopReason = exact.StopReason.ToString() });
        }
        MapLogCollector.Instance.Append(MapLogCategory.GateDetection,
            confirmed.Count == 0 ? MapLogLevel.Warning : MapLogLevel.Info,
            "扫描门复核汇总", details: new()
            {
                ["coarseCount"] = coarse.Gates.Count, ["confirmedCount"] = confirmed.Count,
                ["complete"] = complete, ["confirmationThreshold"] = threshold,
                ["coarseThreshold"] = Math.Min(threshold, .55), ["confirmations"] = confirmations,
                ["sourceWidth"] = image.Width, ["sourceHeight"] = image.Height,
                ["computeStopReason"] = ScanExecutionContext.Current?.ComputeStopReason
            });
        return AddOcclusionRecovery(new GateDetectionResult
        {
            Asset = coarse.Asset,
            ScaleEvidence = scaleEvidence,
            Gates = !complete ? [] : GateTemplateDetector.ClusterAcrossScales(confirmed)
                .Select(group => group.OrderByDescending(g => g.Score).First()).ToArray(),
            RawCandidates = confirmed, SearchModeUsed = GateSearchMode.FullSearch,
            StopReason = search.CancellationToken.IsCancellationRequested ? GateSearchStopReason.Canceled
                : complete ? GateSearchStopReason.Completed : GateSearchStopReason.BudgetExceeded,
            BudgetExceeded = !complete, MatchTemplateCalls = calls, ScalesEvaluated = scales,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
        }, colorImage, viewport, clientWidth, search);
    }

    private GateDetectionResult AddOcclusionRecovery(GateDetectionResult strict, Mat? colorImage,
        MapScreenRect viewport, double clientWidth, GateSearchContext search)
    {
        if (strict.Gates.Count>0 || strict.BudgetExceeded || colorImage is null
            || !GateTemplateRules.EnablePlayerOcclusionRecovery) return strict;
        var remaining = ScanExecutionContext.Current is { IsAutomatic:true } scan
            ? scan.RemainingMilliseconds-60 : 160;
        if(search.TimeBudgetMilliseconds is { } explicitBudget)
            remaining=Math.Min(remaining,explicitBudget-(int)Math.Ceiling(strict.ElapsedMilliseconds));
        if (remaining<=0) return strict;
        var recovery=_gateDetector.DetectPlayerOccludedGates(colorImage,viewport,clientWidth,
            new() {TimeBudgetMilliseconds=Math.Min(160,remaining),CancellationToken=search.CancellationToken});
        MapLogCollector.Instance.Append(MapLogCategory.GateDetection,MapLogLevel.Info,
            "玩家遮挡出口独立复核",elapsedMs:recovery.ElapsedMilliseconds,details:new()
            { ["asset"]=recovery.Asset,["proposals"]=recovery.OcclusionEvidence,
                ["confirmedCount"]=recovery.Gates.Count,["stopReason"]=recovery.StopReason.ToString() });
        return new() { Asset=strict.Asset,ScaleEvidence=strict.ScaleEvidence,OcclusionEvidence=recovery.OcclusionEvidence,
            Gates=recovery.Gates,RawCandidates=strict.RawCandidates.Concat(recovery.RawCandidates).ToArray(),
            SearchModeUsed=strict.SearchModeUsed,StopReason=recovery.StopReason,
            BudgetExceeded=recovery.BudgetExceeded,MatchTemplateCalls=strict.MatchTemplateCalls+recovery.MatchTemplateCalls,
            ScalesEvaluated=strict.ScalesEvaluated,RegionsEvaluated=strict.RegionsEvaluated,
            ElapsedMilliseconds=strict.ElapsedMilliseconds+recovery.ElapsedMilliseconds };
    }

}
