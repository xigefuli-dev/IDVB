using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureEvaluator
{
    internal static double Distance(
        double firstX,
        double firstY,
        double secondX,
        double secondY) =>
        Math.Sqrt(
            Math.Pow(firstX - secondX, 2d)
            + Math.Pow(firstY - secondY, 2d));

    internal static MapStructureCandidate Evaluate(
        QueryGeometry query,
        MapStructureFeatures reference,
        Mat referenceDistance,
        MapStructureRegistrationRequest request,
        double scale,
        int referenceX,
        int referenceY,
        bool usedGlobalSearch,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale,
        Mat? matchingDistance = null,
        Mat? matchingStructure = null,
        Mat? matchingEdges = null,
        int matchingOriginX = 0,
        int matchingOriginY = 0,
        double? projectionCorrelation = null)
    {
        // 当参考图被降采样以匹配低分辨率截帧时，将匹配坐标
        // 映射回原始参考图坐标，保证输出 transform 正确。
        var referenceScale = reciprocalScale.ReferenceScale;
        var actualScale = scale * referenceScale;
        var logicalReferenceX = referenceX - matchingOriginX;
        var logicalReferenceY = referenceY - matchingOriginY;
        var originalRefX = logicalReferenceX / referenceScale;
        var originalRefY = logicalReferenceY / referenceScale;

        using var queryEdges = new Mat(query.Edges, query.Bounds);
        using var queryStructure = new Mat(query.Structure, query.Bounds);
        using var distancePatch = new Mat(
            matchingDistance ?? referenceDistance,
            new Rect(
                referenceX,
                referenceY,
                query.Bounds.Width,
                query.Bounds.Height));
        var structureForPatch = matchingStructure
            ?? reciprocalScale.StructureMask
            ?? reference.StructureMask;
        var edgesForPatch = matchingEdges
            ?? reciprocalScale.Edges
            ?? reference.Edges;
        using var referenceStructurePatch = new Mat(
            structureForPatch,
            new Rect(
                referenceX,
                referenceY,
                query.Bounds.Width,
                query.Bounds.Height));
        // Low-structure hypotheses span a much wider scale range, so their
        // reference-coordinate Chamfer values must be compared in screen
        // pixels. Keep the standard channel's established calibration intact.
        var chamfer = ResolveChamferPixels(
            Cv2.Mean(distancePatch, queryEdges).Val0,
            scale,
            request.Channel);
        using var visibleMaskPatch = query.VisibleMask is not null
            && !query.VisibleMask.Empty()
            ? new Mat(query.VisibleMask, query.Bounds)
            : null;
        using var visibleReferenceStructure = new Mat();
        if (visibleMaskPatch is not null)
            Cv2.BitwiseAnd(
                referenceStructurePatch,
                visibleMaskPatch,
                visibleReferenceStructure);
        else
            referenceStructurePatch.CopyTo(visibleReferenceStructure);

        var asymmetricObserved = request.PreparedLive?.RawVisibleMask is not null
            && request.PreparedLive.DiagnosticTiming?.Profile ==
                MapStructurePreprocessingProfile.NativeObservedStructureLine;
        var reverseChamfer = chamfer;
        using var visibleReferenceEdges = new Mat();
        if (request.Channel == MapAlignmentChannel.LowStructure
            && !asymmetricObserved)
        {
            using var referenceEdgesPatch = new Mat(
                edgesForPatch,
                new Rect(referenceX, referenceY,
                    query.Bounds.Width, query.Bounds.Height));
            if (visibleMaskPatch is not null)
                Cv2.BitwiseAnd(
                    referenceEdgesPatch,
                    visibleMaskPatch,
                    visibleReferenceEdges);
            else
                referenceEdgesPatch.CopyTo(visibleReferenceEdges);
            if (Cv2.CountNonZero(visibleReferenceEdges) == 0)
            {
                reverseChamfer = double.PositiveInfinity;
            }
            else
            {
                var queryDistance = query.GetOrCreateEdgeDistanceMap();
                reverseChamfer = ResolveChamferPixels(
                    Cv2.Mean(queryDistance, visibleReferenceEdges).Val0,
                    scale,
                    request.Channel);
            }
        }
        using var withinTolerance = new Mat();
        using var coveredEdges = new Mat();
        Cv2.Compare(
            distancePatch,
            ResolveEdgeTolerancePixels(
                tuning.EdgeDistanceTolerancePixels,
                scale,
                request.Channel),
            withinTolerance,
            CmpTypes.LE);
        Cv2.BitwiseAnd(withinTolerance, queryEdges, coveredEdges);
        var covered = Cv2.CountNonZero(coveredEdges);
        var edgeCoverage = covered / (double)Math.Max(1, query.EdgeCount);

        using var occupancyOverlap = new Mat();
        Cv2.BitwiseAnd(
            visibleReferenceStructure,
            queryStructure,
            occupancyOverlap);
        var queryStructureCount = Cv2.CountNonZero(queryStructure);
        var overlapCount = Cv2.CountNonZero(occupancyOverlap);
        // ObservedEdges is an asymmetric line-map contract. Its occupancy uses
        // the same physical tolerance as edge coverage, not exact pixel overlap.
        var occupancyCoverage = (asymmetricObserved ? covered : overlapCount)
            / (double)Math.Max(1, queryStructureCount);
        // 反向覆盖：query 覆盖的参考结构 / 整个参考图结构。正向三项指标均以
        // query 归一化，query 越小越稀疏越容易拿高分，系统性偏向更大 scale
        // （更小 query）；本项以「整个参考图结构」为分母，惩罚「参考图大量
        // 墙体未被这个偏小的 query 解释」，使正确 scale 不再被错误 scale
        // 超越（根因②）。
        // Only reference structure visible in the projected viewport belongs
        // in the denominator. Whole-canvas coverage couples the score to file
        // dimensions and systematically favors undersized queries.
        var visibleReferenceStructureCount = Cv2.CountNonZero(
            visibleReferenceStructure);
        var referenceCoverage = visibleReferenceStructureCount > 0
            ? overlapCount / (double)visibleReferenceStructureCount
            : 0d;
        var projection = asymmetricObserved
            ? 1d
            : projectionCorrelation ?? (request.Channel ==
            MapAlignmentChannel.LowStructure
                ? MapStructureProjectionScorer.Score(
                    queryEdges,
                    visibleReferenceEdges,
                    0,
                    0,
                    tuning.LowStructureTranslationTopK)
                : 1d);

        var partitionCounts = new int[4];
        var partitionCovered = new int[4];
        var halfWidth = query.Bounds.Width / 2;
        var halfHeight = query.Bounds.Height / 2;
        var partitions = new[]
        {
            new Rect(0, 0, halfWidth, halfHeight),
            new Rect(
                halfWidth,
                0,
                query.Bounds.Width - halfWidth,
                halfHeight),
            new Rect(
                0,
                halfHeight,
                halfWidth,
                query.Bounds.Height - halfHeight),
            new Rect(
                halfWidth,
                halfHeight,
                query.Bounds.Width - halfWidth,
                query.Bounds.Height - halfHeight)
        };
        for (var index = 0; index < partitions.Length; index++)
        {
            using var partitionEdges = new Mat(queryEdges, partitions[index]);
            using var partitionOverlap = new Mat(
                coveredEdges,
                partitions[index]);
            partitionCounts[index] = Cv2.CountNonZero(partitionEdges);
            partitionCovered[index] = Cv2.CountNonZero(partitionOverlap);
        }
        var isLowStructure = request.Channel == MapAlignmentChannel.LowStructure;
        var minimumEdgesPerPartition = isLowStructure
            ? tuning.MinimumEdgesPerPartition
            : StructureRegistrationRules.MinEdgesPerPartition;
        var minimumPartitionCoverage = isLowStructure
            ? tuning.MinimumPartitionCoverage
            : StructureRegistrationRules.MinPartitionCoverage;
        var edgeCoverageWeight = isLowStructure
            ? tuning.EdgeCoverageWeight
            : StructureRegistrationRules.EdgeCoverageWeight;
        var occupancyCoverageWeight = isLowStructure
            ? tuning.OccupancyCoverageWeight
            : StructureRegistrationRules.OccupancyCoverageWeight;
        var referenceCoverageWeight = isLowStructure
            ? tuning.ReferenceCoverageWeight
            : StructureRegistrationRules.EdgeCoverageWeight;
        var partitionPenaltyWeight = isLowStructure
            ? tuning.PartitionPenaltyWeight
            : StructureRegistrationRules.PartitionPenaltyWeight;
        var priorDisagreementWeight = isLowStructure
            ? tuning.PriorDisagreementWeight
            : StructureRegistrationRules.PriorDisagreementPenaltyWeight;
        var boundsPenalty = isLowStructure
            ? tuning.BoundsPenalty
            : StructureRegistrationRules.BoundsPenalty;
        var chamferWeight = isLowStructure ? tuning.ChamferWeight : 1d;
        var consistentPartitions = Enumerable.Range(0, 4)
            .Count(index => partitionCounts[index] >= minimumEdgesPerPartition
                && partitionCovered[index] / (double)partitionCounts[index]
                    >= minimumPartitionCoverage);
        var composite = (chamfer * chamferWeight)
            + (isLowStructure
                ? Math.Min(
                    reverseChamfer,
                    tuning.MaximumChamferPixels * 4d)
                    * 0.10d
                : 0d)
            + ((1d - edgeCoverage) * edgeCoverageWeight)
            + ((1d - occupancyCoverage) * occupancyCoverageWeight)
            + ((1d - referenceCoverage) * referenceCoverageWeight)
            // Projection is a deliberately lossy one-dimensional summary.
            // Keep it as a ranking hint, never as the fact that vetoes an
            // otherwise strong bidirectional two-dimensional match.
            + (isLowStructure
                ? Math.Max(
                    0d,
                    tuning.LowStructureMinimumProjectionCorrelation
                        - projection)
                    * tuning.PartitionPenaltyWeight
                : 0d)
            + (Math.Max(
                0,
                tuning.MinimumConsistentPartitions - consistentPartitions)
                * partitionPenaltyWeight);
        // offset 在匹配空间中计算：匹配空间里 query 和降采样参考图都是 1:1 对屏幕像素
        var (offsetX, offsetY) = MapCanonicalTransformMath.ComputeScreenOffset(
            request.ViewportBounds.X,
            request.ViewportBounds.Y,
            query.Bounds.X,
            query.Bounds.Y,
            logicalReferenceX,
            logicalReferenceY,
            scale);
        // 原始参考图尺寸（降采样前）
        var originalRefWidth = (int)Math.Round(reference.Edges.Width / referenceScale);
        var originalRefHeight = (int)Math.Round(reference.Edges.Height / referenceScale);
        var bounds = request.ValidMapBounds?.IsValid is true
            ? request.ValidMapBounds
            : MapReferenceBounds.FullImage(
                originalRefWidth,
                originalRefHeight);
        var viewportOrigin = MapCanonicalTransformMath.ComputeViewportOrigin(
            request.ViewportBounds.X,
            request.ViewportBounds.Y,
            offsetX,
            offsetY,
            actualScale);
        var boundsTolerance = 2d / actualScale;
        // 用原始参考图坐标做边界检查
        var isWithinBounds = isLowStructure
            ? originalRefX < bounds.Right + boundsTolerance
                && originalRefY < bounds.Bottom + boundsTolerance
                && originalRefX + (query.Bounds.Width / referenceScale)
                    > bounds.X - boundsTolerance
                && originalRefY + (query.Bounds.Height / referenceScale)
                    > bounds.Y - boundsTolerance
            : originalRefX >= bounds.X - boundsTolerance
            && originalRefY >= bounds.Y - boundsTolerance
            && originalRefX + (query.Bounds.Width / referenceScale)
                <= bounds.Right + boundsTolerance
            && originalRefY + (query.Bounds.Height / referenceScale)
                <= bounds.Bottom + boundsTolerance;
        var predicted = request.PredictedViewportOrigin;
        if (predicted is null
            && request.PlayerPrior is { } playerPrior)
        {
            predicted = MapSessionRules.PredictViewportOrigin(
                playerPrior,
                request.ViewportBounds.Width,
                request.ViewportBounds.Height,
                actualScale,
                bounds);
        }
        var priorAgreement = 1d;
        if (predicted is { } expectedOrigin)
        {
            var diagonal = Math.Sqrt(
                (bounds.Width * bounds.Width)
                + (bounds.Height * bounds.Height));
            priorAgreement = 1d - Math.Clamp(
                Distance(
                    viewportOrigin.X,
                    viewportOrigin.Y,
                    expectedOrigin.X,
                    expectedOrigin.Y)
                / Math.Max(
                    1d,
                    diagonal * tuning.MaximumPlayerPriorDistanceRatio),
                0d,
                1d);
        }
        if (!isWithinBounds)
            composite += boundsPenalty;
        composite += (1d - priorAgreement)
            * priorDisagreementWeight;
        return new MapStructureCandidate
        {
            Scale = actualScale,
            ReferenceX = (int)Math.Round(originalRefX),
            ReferenceY = (int)Math.Round(originalRefY),
            OffsetX = offsetX,
            OffsetY = offsetY,
            ChamferPixels = chamfer,
            ReverseChamferPixels = reverseChamfer,
            EdgeCoverage = edgeCoverage,
            OccupancyCoverage = occupancyCoverage,
            ReferenceCoverage = referenceCoverage,
            ProjectionCorrelation = projection,
            ConsistentPartitions = consistentPartitions,
            UsedGlobalSearch = usedGlobalSearch,
            CompositeCost = composite,
            PriorAgreement = priorAgreement,
            IsWithinValidBounds = isWithinBounds
        };
    }

    internal static double NormalizeChamferToScreenPixels(
        double referencePixels,
        double hypothesisScale) =>
        referencePixels * hypothesisScale;

    internal static double ConvertScreenToleranceToReferencePixels(
        double screenPixels,
        double hypothesisScale) =>
        screenPixels / hypothesisScale;

    internal static double ResolveChamferPixels(
        double referencePixels,
        double hypothesisScale,
        MapAlignmentChannel channel) =>
        channel == MapAlignmentChannel.LowStructure
            ? NormalizeChamferToScreenPixels(referencePixels, hypothesisScale)
            : referencePixels;

    internal static double ResolveEdgeTolerancePixels(
        double configuredPixels,
        double hypothesisScale,
        MapAlignmentChannel channel) =>
        channel == MapAlignmentChannel.LowStructure
            ? ConvertScreenToleranceToReferencePixels(
                configuredPixels,
                hypothesisScale)
            : configuredPixels;
}
/*
 * 文件职责：MapStructureEvaluator。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
