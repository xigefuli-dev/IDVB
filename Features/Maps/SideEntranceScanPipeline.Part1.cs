using OpenCvSharp;
using IDVBuff.Pipeline;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;
/// <summary>
/// 侧门专属扫描管线：对捕获帧运行结构线几何推理，返回 TopK 候选地图。
/// 与双门管线并列，仅用于首次地图识别，对齐阶段仍由原有管线处理。
/// </summary>
public sealed partial class SideEntranceScanPipeline
{
    private IReadOnlyList<SideEntranceScanCandidate> RunSingleGateScan(
        Mat capturedFrame,
        IReadOnlyList<(MapRecord map, string floorKey, Mat featureTemplate)> candidates,
        int topK,
        GateDetection? detectedGate,
        MapScreenRect? viewportBounds,
        bool maskDetectedGate,
        int? gateIndexForDiagnostics,
        Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(capturedFrame);
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK < 1)
            topK = 1;
        if (capturedFrame.Empty() || candidates.Count == 0)
            return [];

        var valid = candidates
            .Where(c => c.featureTemplate is not null && !c.featureTemplate.Empty())
            .ToList();
        if (valid.Count == 0)
            return [];

        if (detectedGate is null || !detectedGate.ScreenBounds.IsValid)
            return [];

        var scanSw = Stopwatch.StartNew();

        using var extractionTrace = MapOperationTraceAmbient.StartChild(
            "side_scan_vpsg_extraction",
            MapOperationWaitKind.Compute);
        using var observation = Vpsg3FastLiveExtractor.Extract(
            capturedFrame,
            viewportBounds,
            maxSparsePoints: 200);
        extractionTrace.Complete();

        var sparsePoints = observation.SparseEdgePoints;
        IReadOnlyList<Point> pointsToTest = sparsePoints;
        if (pointsToTest.Count == 0)
        {
            // 防御性退避：针对测试合成帧或非标准配色帧，使用 Canny 提取基础稀疏边缘点
            using var gray = new Mat();
            if (capturedFrame.Channels() > 1)
                Cv2.CvtColor(capturedFrame, gray, ColorConversionCodes.BGR2GRAY);
            else
                capturedFrame.CopyTo(gray);
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 50, 150);
            using var nonZero = edges.FindNonZero();
            if (!nonZero.Empty())
            {
                var count = nonZero.Total();
                var step = Math.Max(1, (int)(count / 200));
                var fallbackPoints = new List<Point>(200);
                for (var idx = 0; idx < count && fallbackPoints.Count < 200; idx += step)
                {
                    fallbackPoints.Add(nonZero.At<Point>(idx));
                }
                pointsToTest = fallbackPoints;
            }
        }
        if (pointsToTest.Count == 0)
            return [];

        var vpX = viewportBounds?.X ?? 0d;
        var vpY = viewportBounds?.Y ?? 0d;
        var gx = detectedGate.ScreenBounds.CenterX - vpX;
        var gy = detectedGate.ScreenBounds.CenterY - vpY;

        var parallelism = Math.Max(1, SideEntranceScanRules.ScanParallelism);
        var candidateResults = new SideEntranceScanCandidate?[valid.Count];
        var completedCount = 0;

        using var matchingTrace = MapOperationTraceAmbient.StartChild(
            "side_structural_scan_matching",
            MapOperationWaitKind.Compute);

        Parallel.For(
            0,
            valid.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                if (scanSw.ElapsedMilliseconds > SideEntranceScanRules.MaximumScanDurationMs)
                    return;

                var (map, floorKey, template) = valid[i];
                var candidate = EvaluateStructuralCandidate(
                    map,
                    floorKey,
                    template,
                    pointsToTest,
                    observation.ValidMask,
                    gx,
                    gy,
                    viewportBounds);

                candidateResults[i] = candidate;
                progress?.Invoke(0.9d * Interlocked.Increment(ref completedCount) / valid.Count);
            });

        matchingTrace.Complete();

        var results = candidateResults
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        if (results.Count == 0)
            return [];

        results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));

        for (var index = 0; index < results.Count; index++)
        {
            var candidate = results[index];
            var previousGap = index > 0
                ? results[index - 1].MatchScore - candidate.MatchScore
                : double.PositiveInfinity;
            var nextGap = index + 1 < results.Count
                ? candidate.MatchScore - results[index + 1].MatchScore
                : double.PositiveInfinity;
            candidate.TemplateMargin = results.Count == 1
                ? candidate.MatchScore
                : Math.Min(previousGap, nextGap);
            ClassifyTemplateEvidence(candidate, detectedGate, viewportBounds);
            if (candidate.Disposition == SideEntranceCandidateDisposition.Rejected)
            {
                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Warning,
                    $"侧门线索已拒绝 · map={candidate.Map.SequenceNumber}#{candidate.FloorKey} "
                    + $"· reason={candidate.RejectionReason} · {candidate.RejectionDetail}",
                    details: new()
                    {
                        ["mapId"] = candidate.Map.Id,
                        ["templateSimilarity"] = candidate.MatchScore,
                        ["templateMargin"] = candidate.TemplateMargin,
                        ["gateSpatialResidualPixels"] = candidate.GateSpatialResidualPixels,
                        ["matchScale"] = candidate.MatchScale,
                        ["rejectionReason"] = candidate.RejectionReason.ToString(),
                        ["gateIndex"] = gateIndexForDiagnostics
                    });
            }
        }

        var eligible = results
            .Where(candidate => candidate.Disposition != SideEntranceCandidateDisposition.Rejected)
            .OrderBy(candidate => double.IsFinite(candidate.GateSpatialResidualPixels)
                && candidate.GateSpatialResidualPixels > 25d ? 1 : 0)
            .ThenByDescending(candidate => candidate.MatchScore)
            .Take(topK)
            .ToList();

        progress?.Invoke(1.0d);
        return eligible;
    }
}
