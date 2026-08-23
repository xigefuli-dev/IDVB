using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureValidator
{
    internal static MapStructureRejectionReason Validate(
        MapStructureCandidate best,
        double margin,
        double requiredMargin,
        MapStructureRegistrationTuning tuning,
        bool restrictedSearch = false)
    {
        if (!best.IsWithinValidBounds)
            return MapStructureRejectionReason.OutsideValidBounds;
        var isLowStructure = tuning.Channel == MapAlignmentChannel.LowStructure;
        if (best.PriorAgreement <= (isLowStructure
                ? tuning.MinimumPriorAgreement
                : StructureRegistrationRules.MinimumPriorAgreement))
            return MapStructureRejectionReason.PlayerPriorMismatch;
        var chamferLimit = restrictedSearch
            ? Math.Min(
                tuning.MaximumChamferPixels,
                tuning.RestrictedSearchMaximumChamferPixels)
            : tuning.MaximumChamferPixels;
        if (best.ChamferPixels > chamferLimit
            || best.EdgeCoverage < tuning.MinimumEdgeCoverage
            || best.OccupancyCoverage < tuning.MinimumOccupancyCoverage)
        {
            return MapStructureRejectionReason.WeakAbsoluteScore;
        }
        if (best.ConsistentPartitions < tuning.MinimumConsistentPartitions)
            return MapStructureRejectionReason.InconsistentStructure;
        if (margin < requiredMargin)
            return MapStructureRejectionReason.AmbiguousCandidates;
        return MapStructureRejectionReason.None;
    }

    internal static MapStructureRejectionReason ValidateAbsolute(
        MapStructureCandidate candidate,
        MapStructureRegistrationTuning tuning,
        bool restrictedSearch = false) =>
        Validate(candidate, 1d, 0d, tuning, restrictedSearch);

    internal static MapStructureRejectionReason ValidateFastConfidence(
        MapStructureConfidenceBreakdown confidence,
        double minimumGeometricLockConfidence) =>
        confidence.GeometricLockConfidence >= minimumGeometricLockConfidence
            ? MapStructureRejectionReason.None
            : MapStructureRejectionReason.WeakAbsoluteScore;

    /// <summary>
    /// Phase 5: 提前终止安全检查。条件比 <see cref="MapStructureRefiner.CanSkipLocalRefinement"/>
    /// 更严格，因为跳过 Legacy 候选生成比跳过 ECC 精修风险更高。
    /// </summary>
    /// <param name="best">Visible-aware 最佳候选（已通过 CompositeCost 阈值）</param>
    /// <param name="secondBestCost">Visible-aware 次佳候选的 CompositeCost（无穷大 = 仅有一个候选）</param>
    /// <param name="tuning">调优参数</param>
    /// <returns>所有条件通过返回 true</returns>
    internal static bool MeetsEarlyTerminationCriteria(
        MapStructureCandidate best,
        double secondBestCost,
        MapStructureRegistrationTuning tuning)
    {
        // 必须在有效地图范围内
        if (!best.IsWithinValidBounds)
            return false;

        // PlayerPrior 不严重冲突（比 Validate 的 0.05 更严格）
        if (best.PriorAgreement <= StructureRegistrationRules.StrictPriorAgreement)
            return false;

        // Chamfer 阈值：比基础阈值严格 15%
        if (best.ChamferPixels
            > tuning.MaximumChamferPixels
                * StructureRegistrationRules.RefinementChamferFactor)
            return false;

        // EdgeCoverage：比基础阈值严格 0.10
        if (best.EdgeCoverage
            < tuning.MinimumEdgeCoverage
                + StructureRegistrationRules.RefinementEdgeCoverageMargin)
            return false;

        // OccupancyCoverage：比基础阈值严格 0.10
        if (best.OccupancyCoverage
            < tuning.MinimumOccupancyCoverage
                + StructureRegistrationRules.RefinementOccupancyMargin)
            return false;

        // PartitionConsistency：比基础阈值多要求 1 个分区
        if (best.ConsistentPartitions
            < Math.Max(StructureRegistrationRules.EarlyTermMinPartitions, tuning.MinimumConsistentPartitions + StructureRegistrationRules.EarlyTermExtraPartitions))
            return false;

        // Top-1 vs Top-2 边际
        if (double.IsFinite(secondBestCost))
        {
            var marginNormalizationFloor = tuning.Channel ==
                MapAlignmentChannel.LowStructure
                    ? tuning.MarginNormalizationFloor
                    : StructureRegistrationRules.MarginNormalizationFloor;
            var margin = (secondBestCost - best.CompositeCost)
                / Math.Max(marginNormalizationFloor, secondBestCost);
            if (margin < tuning.MinimumCandidateMargin * StructureRegistrationRules.EarlyTermMarginFactor)
                return false;
        }

        return true;
    }

    internal static MapOverlayTransform BuildTransform(
        MapStructureCandidate candidate,
        MapStructureRegistrationRequest request,
        MapStructureFeatures reference)
    {
        var referenceCenterX = reference.Edges.Width / 2d;
        var referenceCenterY = reference.Edges.Height / 2d;
        return new MapOverlayTransform
        {
            ScaleX = candidate.Scale,
            ScaleY = candidate.Scale,
            OffsetX = candidate.OffsetX,
            OffsetY = candidate.OffsetY,
            ReferenceCenterX = referenceCenterX,
            ReferenceCenterY = referenceCenterY,
            ScreenCenterX = (referenceCenterX * candidate.Scale) + candidate.OffsetX,
            ScreenCenterY = (referenceCenterY * candidate.Scale) + candidate.OffsetY,
            ReferenceWidth = reference.Edges.Width,
            ReferenceHeight = reference.Edges.Height,
            OrientationDegrees = 0,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = request.Channel ==
                MapAlignmentChannel.LowStructure
                    ? candidate.ChamferPixels
                    : candidate.ChamferPixels * candidate.Scale
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // 输入验证：检查对齐模式、旋转、缩放。通过时返回 null。
    // ═══════════════════════════════════════════════════════════════

    internal static MapStructureRegistrationResult? ValidateRequest(
        MapStructureRegistrationRequest request,
        bool usedRestrictedSearch)
    {
        // 参考图像素只在缺少 PreparedReference 时才会被用到（Registrar 会现场
        // 预处理它）。结构缓存常驻命中的调用方不再解码 PNG，此时 ReferenceImage
        // 为空是正常输入，不能据此判定 InvalidInput。
        if ((request.PreparedReference is null && request.ReferenceImage.Empty())
            || request.LiveRoi.Empty()
            || !request.ViewportBounds.IsValid)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InvalidInput,
                usedRestrictedSearch: usedRestrictedSearch);
        }
        if (request.LockedTransform.AlignmentMode != MapOverlayAlignmentMode.Uniform
            || Math.Abs(
                request.LockedTransform.ScaleX
                - request.LockedTransform.ScaleY) > StructureRegistrationRules.ScaleDiffTolerance)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.UnsupportedAlignmentMode,
                usedRestrictedSearch: usedRestrictedSearch);
        }
        var normalizedRotation =
            ((request.FixedRotationDegrees % 360d) + 360d) % 360d;
        if (Math.Min(
                normalizedRotation,
                Math.Abs(360d - normalizedRotation)) > StructureRegistrationRules.RotationTolerance)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.UnsupportedAlignmentMode,
                "当前结构配准仅支持已标定的 0° 原生地图旋转。",
                usedRestrictedSearch: usedRestrictedSearch);
        }
        var baselineScale = request.LockedTransform.ScaleX;
        var minimumUsableScale = request.Channel ==
            MapAlignmentChannel.LowStructure
                ? request.Tuning.MinimumUsableScale
                : StructureRegistrationRules.MinimumUsableScale;
        if (!double.IsFinite(baselineScale)
            || baselineScale <= minimumUsableScale)
        {
            return MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.InvalidLockedScale,
                usedRestrictedSearch: usedRestrictedSearch);
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // 统一结果构建器：覆盖 Legacy + Fast 两条路径的所有字段
    // ═══════════════════════════════════════════════════════════════

    internal static MapStructureRegistrationResult BuildResult(
        MapStructureRejectionReason rejectionReason,
        MapStructureCandidate[]? candidates = null,
        double preprocessMs = 0d,
        double searchMs = 0d,
        double refineMs = 0d,
        double distanceMapMs = 0d,
        double queryConstructionMs = 0d,
        double historyCandidateMs = 0d,
        double featureVotingMs = 0d,
        double pyramidSearchMs = 0d,
        double localTemplateSearchMs = 0d,
        double globalTemplateSearchMs = 0d,
        double candidateRankingMs = 0d,
        string? debugDirectory = null,
        double lockedScale = 0d,
        int referenceWidth = 0,
        int referenceHeight = 0,
        int queryEdgePixels = 0,
        Rect? queryBounds = null,
        int scaleHypothesisCount = 0,
        int oversizedHypothesisCount = 0,
        bool usedRestrictedSearch = false,
        bool accepted = false,
        MapOverlayTransform? transform = null,
        double confidence = 0d,
        MapStructureConfidenceBreakdown? confidenceBreakdown = null,
        double bestScore = 0d,
        double secondScore = 0d,
        double candidateMargin = 0d,
        bool wasForcedBestCandidate = false,
        int featureMatchCount = 0,
        int featureInlierCount = 0,
        double featureConsensus = 0d,
        bool eccConverged = false,
        double eccCorrelation = 0d,
        double visibleMaskMs = 0d,
        double visibleFraction = 0d,
        int visibleStructurePixels = 0,
        int visibleEdgePixels = 0,
        double visibleAwareSearchMs = 0d,
        int visibleAwareCandidateCount = 0,
        double visibleAwareTopCost = 0d,
        double visibleAwareTopMargin = 0d,
        bool visibleAwareEarlyAccepted = false,
        string? visibleAwareFallbackReason = null,
        string visibleAwareRequestedBackend = "",
        string visibleAwareActualBackend = "",
        string? visibleAwareUMatFallbackReason = null,
        double visibleAwareCoarseMs = 0d,
        double visibleAwareRefineMs = 0d,
        double visibleAwareUploadMs = 0d,
        double visibleAwareDownloadMs = 0d,
        int visibleAwareCompletedScales = 0,
        int visibleAwareBudgetSkippedScales = 0,
        int visibleAwareCoarsePeaks = 0,
        int visibleAwareRefinedCandidates = 0,
        bool usedFastStrategy = false,
        double fastCoarseSearchMs = 0d,
        int fastCoarseCandidateCount = 0)
    {
        var failureReason = rejectionReason == MapStructureRejectionReason.None
            ? string.Empty
            : rejectionReason.ToDisplayText();
        return new MapStructureRegistrationResult
        {
            Accepted = accepted,
            Transform = transform,
            Confidence = confidence,
            ConfidenceBreakdown = confidenceBreakdown,
            BestScore = bestScore,
            SecondScore = secondScore,
            CandidateMargin = candidateMargin,
            RejectionReason = rejectionReason,
            FailureReason = failureReason,
            Candidates = candidates ?? [],
            PreprocessMilliseconds = preprocessMs,
            SearchMilliseconds = searchMs,
            RefineMilliseconds = refineMs,
            DistanceMapMilliseconds = distanceMapMs,
            QueryConstructionMilliseconds = queryConstructionMs,
            HistoryCandidateMilliseconds = historyCandidateMs,
            FeatureVotingMilliseconds = featureVotingMs,
            PyramidSearchMilliseconds = pyramidSearchMs,
            LocalTemplateSearchMilliseconds = localTemplateSearchMs,
            GlobalTemplateSearchMilliseconds = globalTemplateSearchMs,
            CandidateRankingMilliseconds = candidateRankingMs,
            DebugOutputDirectory = debugDirectory,
            LockedScale = lockedScale,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            QueryEdgePixels = queryEdgePixels,
            QueryBoundsX = queryBounds?.X ?? 0,
            QueryBoundsY = queryBounds?.Y ?? 0,
            QueryBoundsWidth = queryBounds?.Width ?? 0,
            QueryBoundsHeight = queryBounds?.Height ?? 0,
            ScaleHypothesisCount = scaleHypothesisCount,
            OversizedHypothesisCount = oversizedHypothesisCount,
            UsedRestrictedSearch = usedRestrictedSearch,
            WasForcedBestCandidate = wasForcedBestCandidate,
            FeatureMatchCount = featureMatchCount,
            FeatureInlierCount = featureInlierCount,
            FeatureConsensus = featureConsensus,
            EccConverged = eccConverged,
            EccCorrelation = eccCorrelation,
            VisibleMaskMilliseconds = visibleMaskMs,
            VisibleFraction = visibleFraction,
            VisibleStructurePixels = visibleStructurePixels,
            VisibleEdgePixels = visibleEdgePixels,
            VisibleAwareSearchMilliseconds = visibleAwareSearchMs,
            VisibleAwareCandidateCount = visibleAwareCandidateCount,
            VisibleAwareTopCost = visibleAwareTopCost,
            VisibleAwareTopMargin = visibleAwareTopMargin,
            VisibleAwareEarlyAccepted = visibleAwareEarlyAccepted,
            VisibleAwareFallbackReason = visibleAwareFallbackReason,
            VisibleAwareRequestedBackend = visibleAwareRequestedBackend,
            VisibleAwareActualBackend = visibleAwareActualBackend,
            VisibleAwareUMatFallbackReason = visibleAwareUMatFallbackReason,
            VisibleAwareCoarseMilliseconds = visibleAwareCoarseMs,
            VisibleAwareRefineMilliseconds = visibleAwareRefineMs,
            VisibleAwareUploadMilliseconds = visibleAwareUploadMs,
            VisibleAwareDownloadMilliseconds = visibleAwareDownloadMs,
            VisibleAwareCompletedScaleCount = visibleAwareCompletedScales,
            VisibleAwareBudgetSkippedScaleCount = visibleAwareBudgetSkippedScales,
            VisibleAwareCoarsePeakCount = visibleAwareCoarsePeaks,
            VisibleAwareRefinedCandidateCount = visibleAwareRefinedCandidates,
            UsedFastStrategy = usedFastStrategy,
            FastCoarseSearchMilliseconds = fastCoarseSearchMs,
            FastCoarseCandidateCount = fastCoarseCandidateCount
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // 诊断数据包：将 ScaleSearchContext + 常见字段聚合为记录，
    // 避免 RegisterLegacy 中每次 BuildResult 调用重复传递 30+ 参数。
    // ═══════════════════════════════════════════════════════════════

    internal readonly record struct LegacyDiagnostics(
        MapStructureScaleSearch.ScaleSearchContext Ctx,
        double PreprocessMs,
        double SearchMs,
        double RefineMs = 0d,
        double CandidateRankingMs = 0d,
        string? DebugDirectory = null,
        double LockedScale = 0d,
        int ReferenceWidth = 0,
        int ReferenceHeight = 0,
        int QueryEdgePixels = 0,
        Rect? QueryBounds = null,
        int ScaleHypothesisCount = 0,
        int OversizedHypothesisCount = 0,
        bool UsedRestrictedSearch = false,
        double VisibleMaskMs = 0d);

    internal static MapStructureRegistrationResult BuildLegacyResult(
        MapStructureRejectionReason rejectionReason,
        LegacyDiagnostics d,
        MapStructureCandidate[]? candidates = null,
        bool accepted = false,
        MapOverlayTransform? transform = null,
        double confidence = 0d,
        MapStructureConfidenceBreakdown? confidenceBreakdown = null,
        double bestScore = 0d,
        double secondScore = 0d,
        double candidateMargin = 0d,
        bool wasForcedBestCandidate = false,
        double featureConsensus = 0d,
        bool eccConverged = false,
        double eccCorrelation = 0d)
    {
        return BuildResult(rejectionReason,
            candidates: candidates,
            preprocessMs: d.PreprocessMs,
            searchMs: d.SearchMs,
            refineMs: d.RefineMs,
            queryConstructionMs: d.Ctx.QueryConstructionMs,
            historyCandidateMs: d.Ctx.HistoryCandidateMs,
            featureVotingMs: d.Ctx.FeatureVotingMs,
            pyramidSearchMs: d.Ctx.PyramidSearchMs,
            localTemplateSearchMs: d.Ctx.LocalTemplateSearchMs,
            globalTemplateSearchMs: d.Ctx.GlobalTemplateSearchMs,
            candidateRankingMs: d.CandidateRankingMs,
            debugDirectory: d.DebugDirectory,
            lockedScale: d.LockedScale,
            referenceWidth: d.ReferenceWidth,
            referenceHeight: d.ReferenceHeight,
            queryEdgePixels: d.QueryEdgePixels,
            queryBounds: d.QueryBounds,
            scaleHypothesisCount: d.ScaleHypothesisCount,
            oversizedHypothesisCount: d.OversizedHypothesisCount,
            usedRestrictedSearch: d.UsedRestrictedSearch,
            accepted: accepted,
            transform: transform,
            confidence: confidence,
            confidenceBreakdown: confidenceBreakdown,
            bestScore: bestScore,
            secondScore: secondScore,
            candidateMargin: candidateMargin,
            wasForcedBestCandidate: wasForcedBestCandidate,
            featureMatchCount: d.Ctx.FeatureMatchCount,
            featureInlierCount: d.Ctx.FeatureInlierCount,
            featureConsensus: featureConsensus,
            eccConverged: eccConverged,
            eccCorrelation: eccCorrelation,
            visibleMaskMs: d.VisibleMaskMs,
            visibleFraction: d.Ctx.VisibleAwareVisibleFraction ?? 0d,
            visibleStructurePixels: d.Ctx.VisibleAwareStructurePixels ?? 0,
            visibleEdgePixels: d.Ctx.VisibleAwareEdgePixels ?? 0,
            visibleAwareSearchMs: d.Ctx.VisibleAwareTotalMs,
            visibleAwareCandidateCount: d.Ctx.VisibleAwareCandidateCount,
            visibleAwareTopCost: d.Ctx.VisibleAwareBestCost,
            visibleAwareTopMargin: d.Ctx.VisibleAwareTopMargin,
            visibleAwareEarlyAccepted: d.Ctx.VisibleAwareEarlyAccepted,
            visibleAwareFallbackReason: d.Ctx.VisibleAwareFallbackReason,
            visibleAwareRequestedBackend: d.Ctx.VisibleAwareSession?.RequestedBackend ?? "",
            visibleAwareActualBackend: d.Ctx.VisibleAwareSession?.ActualBackend ?? "",
            visibleAwareUMatFallbackReason: d.Ctx.VisibleAwareSession?.FallbackReason,
            visibleAwareCoarseMs: d.Ctx.VisibleAwareCoarseMs,
            visibleAwareRefineMs: d.Ctx.VisibleAwareRefineMs,
            visibleAwareUploadMs: d.Ctx.VisibleAwareSession?.UploadMilliseconds ?? 0d,
            visibleAwareDownloadMs: d.Ctx.VisibleAwareSession?.DownloadMilliseconds ?? 0d,
            visibleAwareCompletedScales: d.Ctx.VisibleAwareCompletedScales,
            visibleAwareBudgetSkippedScales: d.Ctx.VisibleAwareBudgetSkippedScales,
            visibleAwareCoarsePeaks: d.Ctx.VisibleAwareCoarsePeaks,
            visibleAwareRefinedCandidates: d.Ctx.VisibleAwareRefinedCandidates);
    }
}
/*
 * 文件职责：MapStructureValidator。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
