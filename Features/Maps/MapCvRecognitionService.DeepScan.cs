using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private GateTemplateDetector? _deepScanGateDetector;
    internal MapRecognitionAttempt AlignDeepScan(CapturedGameFrame frame,
        SideEntranceScanCandidate candidate, MapAlignmentSession seed,
        MapRecognitionTuning tuning, MapStructureRegistrationTuning structureTuning)
    {
        var context = ScanExecutionContext.Current;
        if (context is not { Policy.Mode: ScanPerformanceMode.DeepScan, CanCompute: true }
            || candidate.StructureIndex is not { } index)
            return new() { FailureReason = "DeepScan 结构验证已取消或超过预算。" };
        using var constraint = context.ConstrainAlignment(index, frame.ViewportBounds);
        // Validate only this floor's fresh local pose. No gate detection, shared
        // learned-scale write, or fallback into another mode's discovery path.
        var attempt = AlignFloorWithoutGates(frame, candidate.Map.Id, candidate.FloorKey,
            seed.LockedTransform, MapOverlayAlignmentMode.Uniform, tuning, structureTuning,
            scaleSearchPolicy: MapScaleSearchPolicy.Fixed,
            identityPriorConfidence: candidate.MatchScore, allowPrimaryFloor: true);
        return context.CanCompute ? attempt : new() { Diagnostics = attempt.Diagnostics,
            FailureReason = "DeepScan 结构验证超过预算，未提交结果。" };
    }

    private void PrewarmDeepScan(IEnumerable<Mat> lines)
    {
        try { _deepScanGateDetector ??= new GateTemplateDetector(MapCvRecognitionHelpers.ResolveGatePath()); }
        catch (Exception ex)
        {
            MapLogCollector.Instance.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                "DeepScan 图标排除器预热失败。", details: new() { ["exception"] = ex.ToString() });
        }
        foreach (var line in lines)
        {
            try { DeepScanStructureIndex.Prewarm(line); }
            catch (Exception ex)
            {
                // Failure must never revoke a successfully built ordinary scan cache.
                MapLogCollector.Instance.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                    "DeepScan 局部索引预热失败，其他扫描模式仍可使用。",
                    details: new() { ["exception"] = ex.ToString() });
            }
        }
    }

    private SideEntranceScanResult RunDeepScan(CapturedGameFrame frame, string? mapClass,
        ScanExecutionContext context, Action<double>? progress)
    {
        var inputs = BuildSideEntranceScanInputs(mapClass);
        var eligible = _maps.Count(map => string.IsNullOrWhiteSpace(mapClass)
            || string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase));
        if (inputs.Count != eligible) context.RetrievalCompleted = false;
        try
        {
            // Gates are optional exclusions, never localization anchors. The dedicated
            // detector owns its warm scale; DeepScan cannot train the ordinary detector.
            var gates = new GateDetectionResult();
            if (_deepScanGateDetector is { } detector && context.CanCompute)
            {
                using var matchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
                gates = detector.Detect(matchImage, frame.ViewportBounds, frame.ClientBounds.Width,
                    GateTemplateRules.EarlyExitScoreThreshold, new GateSearchContext
                    {
                        Mode = GateSearchMode.FullSearch,
                        TimeBudgetMilliseconds = Math.Max(1, Math.Min(120, context.RemainingMilliseconds - 600)),
                        AllowSingleGateEarlyExit = true,
                        SingleGateScoreThreshold = GateTemplateRules.EarlyExitScoreThreshold
                    });
            }
            var candidates = DeepScanPipeline.Run(frame.Image, inputs, frame.ViewportBounds, context, progress, gates.Gates);
            return new()
            {
                GateDetection = gates, Candidates = candidates, EligibleMapCount = eligible, ReadyMapCount = inputs.Count,
                RetrievalWasRun = true,
                FailureStage = candidates.Count == 0 ? SideEntranceFailureStage.Retrieval : SideEntranceFailureStage.None,
                FailureReason = candidates.Count == 0
                    ? "DeepScan 未找到足够的局部拐角结构，请多露出一小片地图后重试。" : string.Empty
            };
        }
        catch (Exception ex)
        {
            context.RetrievalCompleted = false;
            MapLogCollector.Instance.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                "DeepScan 本次扫描失败。", details: new() { ["exception"] = ex.ToString() });
            return new() { EligibleMapCount = eligible, ReadyMapCount = inputs.Count,
                RetrievalWasRun = true, FailureStage = SideEntranceFailureStage.Retrieval,
                FailureReason = "DeepScan 局部结构扫描失败，请重试或选择其他扫描模式。" };
        }
    }
}
