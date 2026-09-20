namespace IDVBuff.Features.Maps;

/// <summary>
/// Builds the persisted research record for one completed high-level
/// alignment route.  Keeping this mapping separate makes success and failure
/// serialization testable without invoking OpenCV or the UI orchestrator.
/// </summary>
public static class MapAlignmentResearchAttemptFactory
{
    public static MapAlignmentResearchAttempt Create(
        MapRecord map,
        string floorKey,
        MapRecognitionAttempt attempt,
        MapRuntimeSettings settings,
        MapSessionSnapshot session,
        MapWindowSignature windowSignature,
        string floorSource,
        MapOverlayTransform? scaleSeed = null,
        IReadOnlyList<double>? searchRadii = null,
        int stableConfirmationFrames = 0,
        int stableConfirmationRequiredFrames = 0,
        bool calibrationUpdated = false,
        string? calibrationRejectionReason = null,
        RuntimeMapRecognition? recognitionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(windowSignature);

        var structure = attempt.StructureResult;
        var recognition = recognitionOverride ?? attempt.Recognition;
        var transform = recognition?.Result.OverlayTransform
            ?? structure?.Transform;
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var learned = settings.FloorScaleCalibrations.FirstOrDefault(candidate =>
            candidate.Matches(map.Id, map.UpdatedAt, primaryFloorKey, floorKey));
        var confidence = recognition?.Result.Confidence
            ?? structure?.Confidence
            ?? 0d;
        var elapsed = attempt.Diagnostics.TotalMilliseconds;
        if (elapsed <= 0d)
        {
            elapsed = (structure?.PreprocessMilliseconds ?? 0d)
                + (structure?.SearchMilliseconds ?? 0d)
                + (structure?.RefineMilliseconds ?? 0d);
        }

        var failureReason = string.IsNullOrWhiteSpace(attempt.FailureReason)
            ? attempt.StructureFailureReason
            : attempt.FailureReason;

        return new MapAlignmentResearchAttempt
        {
            SessionVersion = session.Version,
            AlignmentRevision = session.AlignmentRevision,
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = floorKey,
            FloorPosition = MapFloorRules.GetFloorPosition(map, floorKey),
            FloorSource = floorSource,
            WindowSignature = windowSignature,
            ReferenceWidth = transform?.ReferenceWidth
                ?? structure?.ReferenceWidth
                ?? 0,
            ReferenceHeight = transform?.ReferenceHeight
                ?? structure?.ReferenceHeight
                ?? 0,
            ValidMapBounds = MapFloorRules.GetFloorProfile(map, floorKey)
                ?.GetEffectiveValidMapBounds(),
            PrimaryScale = scaleSeed?.ScaleX
                ?? (string.Equals(floorKey, primaryFloorKey, StringComparison.Ordinal)
                    ? transform?.ScaleX
                    : null),
            HistoricalFloorRatio = learned?.MedianRatio,
            ScaleSeedSource = string.IsNullOrWhiteSpace(
                    attempt.Diagnostics.ScaleSeedSource)
                ? scaleSeed is null ? "double-gate" : "cross-floor"
                : attempt.Diagnostics.ScaleSeedSource,
            ScaleSeedCacheSource = attempt.Diagnostics.ScaleSeedCacheSource,
            ScaleSeedProjected = attempt.Diagnostics.ScaleSeedProjected,
            ScaleSeedSourceViewportWidth =
                attempt.Diagnostics.ScaleSeedSourceViewportWidth,
            ScaleSeedSourceViewportHeight =
                attempt.Diagnostics.ScaleSeedSourceViewportHeight,
            ScaleSeedTargetViewportWidth =
                attempt.Diagnostics.ScaleSeedTargetViewportWidth,
            ScaleSeedTargetViewportHeight =
                attempt.Diagnostics.ScaleSeedTargetViewportHeight,
            ProjectedScale = attempt.Diagnostics.ProjectedScale > 0d
                ? attempt.Diagnostics.ProjectedScale
                : null,
            FinalValidatedScale = attempt.Diagnostics.FinalValidatedScale > 0d
                ? attempt.Diagnostics.FinalValidatedScale
                : null,
            ScaleSeedRejectionReason =
                attempt.Diagnostics.ScaleSeedRejectionReason,
            SearchStages = (searchRadii ?? [])
                .Select(radius => new MapAlignmentResearchSearchStage(
                    radius,
                    structure?.ScaleHypothesisCount ?? 0,
                    UsedGlobalTranslationSearch: structure?.LowStructureRoute
                        is nameof(LowStructureAlignmentRoute.ShapeSeed)
                        or nameof(LowStructureAlignmentRoute.SparseCoarseSeed)
                        or nameof(LowStructureAlignmentRoute.IncrementalRecovery)
                        ? structure.LowStructureTranslationCandidateCount > 0
                        : true))
                .ToArray(),
            AlignmentRoute = attempt.Diagnostics.LowStructureRoute,
            ReadinessDecision = attempt.Diagnostics.LowStructureReadinessDecision,
            LowStructureCacheTrustLevel =
                attempt.Diagnostics.LowStructureCacheTrustLevel,
            LowStructurePlannedScaleCount =
                attempt.Diagnostics.LowStructurePlannedScaleCount,
            LowStructureCompletedScaleCount =
                attempt.Diagnostics.LowStructureCompletedScaleCount,
            LowStructureRecoveryBatch =
                attempt.Diagnostics.LowStructureRecoveryBatch,
            LowStructureRecoveryTotalScaleCount =
                attempt.Diagnostics.LowStructureRecoveryTotalScaleCount,
            LowStructureTranslationCandidateCount =
                attempt.Diagnostics.LowStructureTranslationCandidateCount,
            LowStructureBudgetTerminationReason =
                attempt.Diagnostics.LowStructureBudgetTerminationReason,
            LowStructureVpsgEnabled =
                attempt.Diagnostics.LowStructureVpsgEnabled,
            VpsgActuallyEnabled = attempt.Diagnostics.VpsgActuallyEnabled,
            StructureSearchMilliseconds =
                attempt.Diagnostics.StructureSearchMilliseconds,
            StructureRefineMilliseconds =
                attempt.Diagnostics.StructureRefineMilliseconds,
            TotalAlignmentMilliseconds = attempt.Diagnostics.TotalMilliseconds,
            QueryEdgePixels = structure?.QueryEdgePixels ?? 0,
            QueryBoundsWidth = structure?.QueryBoundsWidth ?? 0,
            QueryBoundsHeight = structure?.QueryBoundsHeight ?? 0,
            FeatureMatchCount = structure?.FeatureMatchCount ?? 0,
            FeatureInlierCount = structure?.FeatureInlierCount ?? 0,
            GateCandidateCount = attempt.Diagnostics.GateCandidateCount,
            AnchorMatches = recognition?.Result.AnchorMatches ?? [],
            EvidenceKind = recognition?.Result.EvidenceKind
                ?? MapAlignmentEvidenceKind.None,
            Candidates = structure?.Candidates.Take(20).ToArray() ?? [],
            ConfidenceBreakdown = structure?.ConfidenceBreakdown,
            FinalTransform = transform,
            Confidence = confidence,
            IsHighConfidence = confidence >= settings.SessionTuning.HighConfidence,
            Accepted = recognition is not null,
            StableConfirmationFrames = stableConfirmationFrames,
            StableConfirmationRequiredFrames = stableConfirmationRequiredFrames,
            CalibrationUpdated = calibrationUpdated,
            CalibrationRejectionReason = calibrationRejectionReason,
            ElapsedMilliseconds = elapsed,
            FailureCategory = recognition is null
                ? MapAlignmentResearchFailureClassifier.Classify(attempt)
                : MapAlignmentResearchFailureCategory.None,
            FailureReason = recognition is null ? failureReason : string.Empty
        };
    }
}
/*
 * 文件职责：MapAlignmentResearchAttemptFactory。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
