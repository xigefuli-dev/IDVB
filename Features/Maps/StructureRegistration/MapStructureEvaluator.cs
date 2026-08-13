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
        MapStructureRegistrar.ReciprocalScaleContext reciprocalScale)
    {
        // 当参考图被降采样以匹配低分辨率截帧时，将匹配坐标
        // 映射回原始参考图坐标，保证输出 transform 正确。
        var referenceScale = reciprocalScale.ReferenceScale;
        var actualScale = scale * referenceScale;
        var originalRefX = referenceX / referenceScale;
        var originalRefY = referenceY / referenceScale;

        using var queryEdges = new Mat(query.Edges, query.Bounds);
        using var queryStructure = new Mat(query.Structure, query.Bounds);
        using var distancePatch = new Mat(
            referenceDistance,
            new Rect(
                referenceX,
                referenceY,
                query.Bounds.Width,
                query.Bounds.Height));
        var structureForPatch = reciprocalScale.StructureMask ?? reference.StructureMask;
        using var referenceStructurePatch = new Mat(
            structureForPatch,
            new Rect(
                referenceX,
                referenceY,
                query.Bounds.Width,
                query.Bounds.Height));
        var chamfer = Cv2.Mean(distancePatch, queryEdges).Val0;
        using var withinTolerance = new Mat();
        using var coveredEdges = new Mat();
        Cv2.Compare(
            distancePatch,
            tuning.EdgeDistanceTolerancePixels,
            withinTolerance,
            CmpTypes.LE);
        Cv2.BitwiseAnd(withinTolerance, queryEdges, coveredEdges);
        var covered = Cv2.CountNonZero(coveredEdges);
        var edgeCoverage = covered / (double)Math.Max(1, query.EdgeCount);

        using var occupancyOverlap = new Mat();
        Cv2.BitwiseAnd(
            referenceStructurePatch,
            queryStructure,
            occupancyOverlap);
        var queryStructureCount = Cv2.CountNonZero(queryStructure);
        var overlapCount = Cv2.CountNonZero(occupancyOverlap);
        var occupancyCoverage = overlapCount
            / (double)Math.Max(1, queryStructureCount);
        // 反向覆盖：query 覆盖的参考结构 / 整个参考图结构。正向三项指标均以
        // query 归一化，query 越小越稀疏越容易拿高分，系统性偏向更大 scale
        // （更小 query）；本项以「整个参考图结构」为分母，惩罚「参考图大量
        // 墙体未被这个偏小的 query 解释」，使正确 scale 不再被错误 scale
        // 超越（根因②）。
        var referenceFullStructureCount = Cv2.CountNonZero(structureForPatch);
        var referenceCoverage = referenceFullStructureCount > 0
            ? overlapCount / (double)referenceFullStructureCount
            : 0d;

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
        var consistentPartitions = Enumerable.Range(0, 4)
            .Count(index => partitionCounts[index] >= StructureRegistrationRules.MinEdgesPerPartition
                && partitionCovered[index] / (double)partitionCounts[index] >= StructureRegistrationRules.MinPartitionCoverage);
        var composite = chamfer
            + ((1d - edgeCoverage) * StructureRegistrationRules.EdgeCoverageWeight)
            + ((1d - occupancyCoverage) * StructureRegistrationRules.OccupancyCoverageWeight)
            + ((1d - referenceCoverage) * StructureRegistrationRules.EdgeCoverageWeight)
            + (Math.Max(
                0,
                tuning.MinimumConsistentPartitions - consistentPartitions)
                * StructureRegistrationRules.PartitionPenaltyWeight);
        // offset 在匹配空间中计算：匹配空间里 query 和降采样参考图都是 1:1 对屏幕像素
        var offsetX = request.ViewportBounds.X
            + (query.Bounds.X * scale)
            - (referenceX * scale);
        var offsetY = request.ViewportBounds.Y
            + (query.Bounds.Y * scale)
            - (referenceY * scale);
        // 原始参考图尺寸（降采样前）
        var originalRefWidth = (int)Math.Round(reference.Edges.Width / referenceScale);
        var originalRefHeight = (int)Math.Round(reference.Edges.Height / referenceScale);
        var bounds = request.ValidMapBounds?.IsValid is true
            ? request.ValidMapBounds
            : MapReferenceBounds.FullImage(
                originalRefWidth,
                originalRefHeight);
        var viewportOrigin = new MapViewportOrigin(
            (request.ViewportBounds.X - offsetX) / actualScale,
            (request.ViewportBounds.Y - offsetY) / actualScale);
        var boundsTolerance = 2d / actualScale;
        // 用原始参考图坐标做边界检查
        var isWithinBounds =
            originalRefX >= bounds.X - boundsTolerance
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
            composite += StructureRegistrationRules.BoundsPenalty;
        composite += (1d - priorAgreement)
            * StructureRegistrationRules.PriorDisagreementPenaltyWeight;
        return new MapStructureCandidate
        {
            Scale = actualScale,
            ReferenceX = (int)Math.Round(originalRefX),
            ReferenceY = (int)Math.Round(originalRefY),
            OffsetX = offsetX,
            OffsetY = offsetY,
            ChamferPixels = chamfer,
            EdgeCoverage = edgeCoverage,
            OccupancyCoverage = occupancyCoverage,
            ConsistentPartitions = consistentPartitions,
            UsedGlobalSearch = usedGlobalSearch,
            CompositeCost = composite,
            PriorAgreement = priorAgreement,
            IsWithinValidBounds = isWithinBounds
        };
    }
}
