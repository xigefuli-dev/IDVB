using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

internal static class MapStructureRefiner
{
    internal const int MaximumEccInputPixels = 500_000;
    internal const int MaximumEccInputDimension = 1024;
    internal const int MinimumEccBudgetMilliseconds = 75;

    internal static bool HasEccBudget(int remainingBudgetMilliseconds) =>
        remainingBudgetMilliseconds >= MinimumEccBudgetMilliseconds;

    internal sealed record EccRefinementDiagnostics(
        bool Executed,
        bool Downsampled,
        int OriginalWidth,
        int OriginalHeight,
        int ExecutionWidth,
        int ExecutionHeight,
        double ExecutionScale,
        string SkipReason)
    {
        internal static readonly EccRefinementDiagnostics NotReached =
            new(false, false, 0, 0, 0, 0, 1d, "not-reached");
    }

    internal static MapStructureCandidate RefineCandidate(
        MapStructureCandidate candidate,
        MapStructureFeatures live,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        int remainingBudgetMilliseconds,
        out EccRefinementDiagnostics eccDiagnostics)
    {
        using var localRefinement = MapOperationTraceAmbient.StartChild(
            "local_structure_refinement",
            MapOperationWaitKind.Compute);
        eccDiagnostics = EccRefinementDiagnostics.NotReached;
        // The ECC switch governs the entire refinement stage.  Previously it
        // only skipped FindTransformECC after the translation grid had already
        // built a query and evaluated 32 neighbours, so a disabled feature
        // still consumed most of its refinement time.
        if (!tuning.EnableEccRefinement)
        {
            eccDiagnostics = eccDiagnostics with { SkipReason = "disabled" };
            return candidate;
        }
        if (candidate.CompositeCost <= StructureRegistrationRules.RefinementEarlyExitScore)
        {
            eccDiagnostics = eccDiagnostics with { SkipReason = "candidate-strong" };
            return candidate;
        }

        // 互逆缩放：candidate 的 ReferenceX/Y 和 Scale 已被 Evaluate
        // 映射到原始参考图空间。RefineCandidate 需要在匹配空间
        // （referenceDistance 所在坐标空间）中操作，避免坐标双重转换。
        var refScale = reciprocalScale.ReferenceScale;
        var matchScale = candidate.Scale / refScale;
        var refWidth = reciprocalScale.StructureMask?.Width ?? reference.Edges.Width;
        var refHeight = reciprocalScale.StructureMask?.Height ?? reference.Edges.Height;

        using var query = MapStructureScaleSearch.CreateQuery(live, request.LiveRoi.Size(), matchScale);
        var best = candidate;
        // Translation-only coarse-to-fine refinement. The search never
        // introduces scale, rotation, affine, or perspective freedom.
        foreach (var step in StructureRegistrationRules.RefinementSteps)
        {
            // 每轮以当前最佳坐标为中心（映射回匹配空间）
            var centerX = (int)Math.Round(best.ReferenceX * refScale);
            var centerY = (int)Math.Round(best.ReferenceY * refScale);
            foreach (var (deltaX, deltaY) in new[]
                     {
                         (-step, -step), (0, -step), (step, -step),
                         (-step, 0), (step, 0),
                         (-step, step), (0, step), (step, step)
                     })
            {
                var x = centerX + deltaX;
                var y = centerY + deltaY;
                if (x < 0
                    || y < 0
                    || x + query.Bounds.Width >= refWidth
                    || y + query.Bounds.Height >= refHeight)
                {
                    continue;
                }
                var evaluated = MapStructureEvaluator.Evaluate(
                    query,
                    reference,
                    referenceDistance,
                    request,
                    matchScale,
                    x,
                    y,
                    candidate.UsedGlobalSearch,
                    tuning,
                    reciprocalScale) with
                {
                    FeatureInlierCount = candidate.FeatureInlierCount,
                    FeatureConsensus = candidate.FeatureConsensus
                };
                if (evaluated.CompositeCost < best.CompositeCost)
                    best = evaluated;
            }
        }
        if (best.CompositeCost <= tuning.SkipEccScoreThreshold)
        {
            eccDiagnostics = eccDiagnostics with { SkipReason = "score-threshold" };
            return best;
        }
        if (!HasEccBudget(remainingBudgetMilliseconds))
        {
            eccDiagnostics = eccDiagnostics with { SkipReason = "budget-exhausted" };
            return best;
        }
        using var ecc = MapOperationTraceAmbient.StartChild(
            "ecc_refinement",
            MapOperationWaitKind.Compute);
        return RefineTranslationWithEcc(
            best,
            query,
            reference,
            out eccDiagnostics);
    }

    internal static bool CanSkipLocalRefinement(
        IReadOnlyList<MapStructureCandidate> ranked,
        MapStructureRegistrationTuning tuning,
        bool restrictedSearch = false)
    {
        if (tuning.EnableEccRefinement || ranked.Count == 0)
            return false;
        var best = ranked[0];
        var secondScore = ranked.Count > 1
            ? ranked[1].CompositeCost
            : double.PositiveInfinity;
        var margin = double.IsPositiveInfinity(secondScore)
            ? 1d
            : Math.Clamp(
                (secondScore - best.CompositeCost)
                / Math.Max(StructureRegistrationRules.MarginNormalizationFloor, secondScore),
                0d,
                1d);
        var requiredMargin = tuning.MinimumCandidateMargin
            * (best.UsedGlobalSearch ? StructureRegistrationRules.GlobalSearchMarginMultiplier : 1d);
        var chamferLimit = restrictedSearch
            ? Math.Min(
                tuning.MaximumChamferPixels,
                tuning.RestrictedSearchMaximumChamferPixels)
            : tuning.MaximumChamferPixels;
        return MapStructureValidator.Validate(
                    best, margin, requiredMargin, tuning, restrictedSearch)
                == MapStructureRejectionReason.None
            && best.ChamferPixels
                <= chamferLimit
                    * StructureRegistrationRules.StrictChamferFactor
            && best.EdgeCoverage
                >= tuning.MinimumEdgeCoverage
                    + StructureRegistrationRules.StrictOccupancyMargin
            && best.OccupancyCoverage
                >= tuning.MinimumOccupancyCoverage
                    + StructureRegistrationRules.StrictOccupancyMargin
            && best.ConsistentPartitions >= Math.Max(
                StructureRegistrationRules.CanSkipRefinementMinPartitions,
                tuning.MinimumConsistentPartitions)
            && margin >= Math.Max(
                StructureRegistrationRules.MinimumReplacementMargin,
                requiredMargin * 2d);
    }

    internal static MapStructureCandidate RefineTranslationWithEcc(
        MapStructureCandidate candidate,
        QueryGeometry query,
        MapStructureFeatures reference)
    {
        return RefineTranslationWithEcc(
            candidate,
            query,
            reference,
            out _);
    }

    internal static MapStructureCandidate RefineTranslationWithEcc(
        MapStructureCandidate candidate,
        QueryGeometry query,
        MapStructureFeatures reference,
        out EccRefinementDiagnostics diagnostics)
    {
        using var ecc = MapOperationTraceAmbient.StartChild(
            "ecc_execution",
            MapOperationWaitKind.Compute);
        var originalWidth = query.Bounds.Width;
        var originalHeight = query.Bounds.Height;
        diagnostics = new EccRefinementDiagnostics(
            false,
            false,
            originalWidth,
            originalHeight,
            originalWidth,
            originalHeight,
            1d,
            string.Empty);
        if (candidate.ReferenceX < 0
            || candidate.ReferenceY < 0
            || candidate.ReferenceX + query.Bounds.Width > reference.Edges.Width
            || candidate.ReferenceY + query.Bounds.Height > reference.Edges.Height)
        {
            diagnostics = diagnostics with { SkipReason = "outside-reference" };
            return candidate;
        }
        try
        {
            using var referencePatch = new Mat(
                reference.Edges,
                new Rect(
                    candidate.ReferenceX,
                    candidate.ReferenceY,
                    query.Bounds.Width,
                    query.Bounds.Height));
            using var queryPatch = new Mat(query.Edges, query.Bounds);
            using var mask = new Mat(query.Structure, query.Bounds);
            using var referenceFloat = new Mat();
            using var queryFloat = new Mat();
            referencePatch.ConvertTo(referenceFloat, MatType.CV_32FC1, 1d / 255d);
            queryPatch.ConvertTo(queryFloat, MatType.CV_32FC1, 1d / 255d);
            var areaScale = Math.Sqrt(
                MaximumEccInputPixels
                / Math.Max(1d, originalWidth * (double)originalHeight));
            var dimensionScale = MaximumEccInputDimension
                / Math.Max(1d, Math.Max(originalWidth, originalHeight));
            var executionScale = Math.Min(1d, Math.Min(areaScale, dimensionScale));
            var executionWidth = Math.Max(
                1,
                (int)Math.Round(originalWidth * executionScale));
            var executionHeight = Math.Max(
                1,
                (int)Math.Round(originalHeight * executionScale));
            using var resizedReference = executionScale < 0.999d ? new Mat() : null;
            using var resizedQuery = executionScale < 0.999d ? new Mat() : null;
            using var resizedMask = executionScale < 0.999d ? new Mat() : null;
            if (resizedReference is not null
                && resizedQuery is not null
                && resizedMask is not null)
            {
                var executionSize = new Size(executionWidth, executionHeight);
                Cv2.Resize(
                    referenceFloat,
                    resizedReference,
                    executionSize,
                    0d,
                    0d,
                    InterpolationFlags.Area);
                Cv2.Resize(
                    queryFloat,
                    resizedQuery,
                    executionSize,
                    0d,
                    0d,
                    InterpolationFlags.Area);
                Cv2.Resize(
                    mask,
                    resizedMask,
                    executionSize,
                    0d,
                    0d,
                    InterpolationFlags.Nearest);
            }
            var executionReference = resizedReference ?? referenceFloat;
            var executionQuery = resizedQuery ?? queryFloat;
            var executionMask = resizedMask ?? mask;
            diagnostics = diagnostics with
            {
                Executed = true,
                Downsampled = executionScale < 0.999d,
                ExecutionWidth = executionWidth,
                ExecutionHeight = executionHeight,
                ExecutionScale = executionScale
            };
            using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
            var correlation = Cv2.FindTransformECC(
                executionReference,
                executionQuery,
                warp,
                MotionTypes.Translation,
                new TermCriteria(
                    CriteriaTypes.Count | CriteriaTypes.Eps,
                    StructureRegistrationRules.EccMaxIterations,
                    StructureRegistrationRules.EccEpsilon),
                executionMask,
                StructureRegistrationRules.EccGaussFocalLen);
            var translationX = warp.At<float>(0, 2) / executionScale;
            var translationY = warp.At<float>(1, 2) / executionScale;
            if (!double.IsFinite(correlation)
                || correlation < StructureRegistrationRules.EccMinCorrelation
                || !double.IsFinite(translationX)
                || !double.IsFinite(translationY)
                || Math.Abs(translationX) > StructureRegistrationRules.EccMaxTranslationShift
                || Math.Abs(translationY) > StructureRegistrationRules.EccMaxTranslationShift)
            {
                diagnostics = diagnostics with { SkipReason = "result-rejected" };
                return candidate;
            }

            // findTransformECC returns the warp from template coordinates to
            // input coordinates (OpenCV aligns with WARP_INVERSE_MAP). For
            // screen = scale * reference + offset, that warp translation is
            // added to the screen offset.
            return candidate with
            {
                OffsetX = candidate.OffsetX
                    + (translationX * candidate.Scale),
                OffsetY = candidate.OffsetY
                    + (translationY * candidate.Scale),
                EccConverged = true,
                EccCorrelation = correlation
            };
        }
        catch (OpenCVException)
        {
            diagnostics = diagnostics with { SkipReason = "opencv-error" };
            return candidate;
        }
    }
}
/*
 * 文件职责：MapStructureRefiner。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
