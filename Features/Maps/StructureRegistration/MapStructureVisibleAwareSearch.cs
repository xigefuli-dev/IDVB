using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal readonly record struct VisibleAwareSearchDiagnostics(
    bool Ran,
    double SearchMilliseconds,
    int CandidateCount,
    double BestCost,
    double SecondCost,
    double VisibleFraction,
    int VisibleStructurePixels,
    int VisibleEdgePixels)
{
    public static readonly VisibleAwareSearchDiagnostics Empty = new();
}

internal static class MapStructureVisibleAwareSearch
{
    internal static VisibleAwareSearchDiagnostics CollectVisibleAwareCandidates(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        List<MapStructureCandidate> candidates)
    {
        // 门控：开关未启用 → 立即返回
        if (!tuning.EnableVisibleAwareShadow
            && !tuning.EnableVisibleAwareInjection)
            return VisibleAwareSearchDiagnostics.Empty;

        // 无 VisibleMask → 返回
        if (query.VisibleMask is null || query.VisibleMask.Empty())
            return VisibleAwareSearchDiagnostics.Empty;

        // 可见像素不足 → 返回
        var totalVisible = Cv2.CountNonZero(query.VisibleMask);
        var visibleFraction = (double)totalVisible
            / (query.VisibleMask.Width * query.VisibleMask.Height);
        if (visibleFraction < tuning.VisibleAwareMinimumVisibleFraction)
            return VisibleAwareSearchDiagnostics.Empty;

        // 裁剪到 query.Bounds
        using var visibleCropped = new Mat(query.VisibleMask, query.Bounds);

        // 腐蚀得到 SafeVisibleMask
        var erodeSize = 1 + tuning.SafeVisibleMaskErodePixels * 2;
        using var erodeKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, new Size(erodeSize, erodeSize));
        using var safeVisible = new Mat();
        Cv2.Erode(visibleCropped, safeVisible, erodeKernel);

        // 派生 VisibleStructure 和 VisibleEdges
        // Crop structure and edges to query.Bounds first so they match
        // safeVisible dimensions. BitwiseAnd requires same-size inputs.
        using var structureCropped = new Mat(query.Structure, query.Bounds);
        using var edgesCropped = new Mat(query.Edges, query.Bounds);
        using var visibleStructure = new Mat();
        Cv2.BitwiseAnd(structureCropped, safeVisible, visibleStructure);

        var visibleStructurePixels = Cv2.CountNonZero(visibleStructure);
        if (visibleStructurePixels < tuning.VisibleAwareMinimumVisibleStructurePixels)
            return VisibleAwareSearchDiagnostics.Empty;

        using var visibleEdges = new Mat();
        Cv2.BitwiseAnd(edgesCropped, safeVisible, visibleEdges);

        // ═══════════════════════════════════════════════════════
        // 两次相关运算生成 IoU 响应图
        // ═══════════════════════════════════════════════════════

        // TP(x) = sum(refStructure * visibleStructure) 对于每个位置 x
        using var tpResponse = new Mat();
        Cv2.MatchTemplate(
            reference.StructureMask,
            visibleStructure,
            tpResponse,
            TemplateMatchModes.CCorr);

        // RefVisibleStructure(x) = sum(refStructure * safeVisible) 对于每个位置 x
        using var refVisStructResponse = new Mat();
        Cv2.MatchTemplate(
            reference.StructureMask,
            safeVisible,
            refVisStructResponse,
            TemplateMatchModes.CCorr);

        // IoU(x) = TP(x) / (LiveStructureCount + RefVisibleStructure(x) - TP(x))
        var liveStructCount = (double)visibleStructurePixels;

        using var tpFloat = new Mat();
        tpResponse.ConvertTo(tpFloat, MatType.CV_32FC1);

        using var refVisFloat = new Mat();
        refVisStructResponse.ConvertTo(refVisFloat, MatType.CV_32FC1);

        using var union = new Mat();
        Cv2.Add(refVisFloat, liveStructCount, union);
        Cv2.Subtract(union, tpFloat, union);
        Cv2.Max(union, 1d, union);  // 避免除零

        using var iouResponse = new Mat();
        Cv2.Divide(tpFloat, union, iouResponse);

        // ═══════════════════════════════════════════════════════
        // Top-K 提取 + NMS
        // ═══════════════════════════════════════════════════════

        var suppressRadius = Math.Max(
            4,
            Math.Min(iouResponse.Width, iouResponse.Height) / 8);
        var rawCandidates = new List<(int X, int Y, double IoU)>();
        var maxK = tuning.VisibleAwareTopK * 3;

        var nmsScores = iouResponse.Clone();
        for (int k = 0; k < maxK; k++)
        {
            Cv2.MinMaxLoc(nmsScores, out _, out var maxVal,
                out _, out var maxLoc);
            if (maxVal <= 0d)
                break;

            rawCandidates.Add((maxLoc.X, maxLoc.Y, maxVal));

            Cv2.Circle(nmsScores, maxLoc, suppressRadius,
                Scalar.All(0d), -1);

            if (rawCandidates.Count >= tuning.VisibleAwareTopK)
                break;
        }
        nmsScores.Dispose();

        // ═══════════════════════════════════════════════════════
        // 映射回完整分辨率 + 通过 Evaluate() 精确评估
        // ═══════════════════════════════════════════════════════

        var visibleEdgePixels = Cv2.CountNonZero(visibleEdges);
        var evaluatedCosts = new List<double>(rawCandidates.Count);

        foreach (var (x, y, iouScore) in rawCandidates)
        {
            // MatchTemplate returns the position of the cropped template
            // (already offset by query.Bounds) in the reference image.
            // Do NOT add query.Bounds.X/Y again — that would double the
            // internal crop offset.
            var refX = x;
            var refY = y;

            // 通过现有 Evaluate() 获取完整可比较评分
            var evaluated = MapStructureEvaluator.Evaluate(
                query, reference, referenceDistance, request, scale,
                refX, refY,
                usedGlobalSearch: true, tuning,
                reciprocalScale);

            evaluatedCosts.Add(evaluated.CompositeCost);

            var candidate = evaluated with
            {
                FromVisibleAware = true,
                VisibleFraction = visibleFraction,
                VisibleStructurePixels = visibleStructurePixels,
                VisibleEdgePixels = visibleEdgePixels
            };

            if (tuning.EnableVisibleAwareInjection)
                candidates.Add(candidate);
        }

        evaluatedCosts.Sort();
        return new VisibleAwareSearchDiagnostics(
            Ran: true,
            SearchMilliseconds: 0d,
            CandidateCount: rawCandidates.Count,
            BestCost: evaluatedCosts.Count > 0
                ? evaluatedCosts[0] : double.PositiveInfinity,
            SecondCost: evaluatedCosts.Count > 1
                ? evaluatedCosts[1] : double.PositiveInfinity,
            VisibleFraction: visibleFraction,
            VisibleStructurePixels: visibleStructurePixels,
            VisibleEdgePixels: visibleEdgePixels);
    }
}
