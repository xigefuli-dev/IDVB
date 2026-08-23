namespace IDVBuff.Features.Maps;

/// <summary>
/// Compatibility carrier for fixed-scale recognition inside one map-open
/// session. It is cleared on close; only calibration scale/rotation may be
/// persisted separately.
/// </summary>
public sealed class MapAlignmentSession
{
    public Guid MapId { get; init; }
    public DateTimeOffset MapUpdatedAt { get; init; }
    public string FloorKey { get; init; } = "1f";
    public MapOverlayTransform LockedTransform { get; init; } = new();
    public IReadOnlyList<CvAnchorEvidence> LockedGateEvidence { get; init; } = [];
    public MapAlignmentTrackingMode Mode { get; init; } = MapAlignmentTrackingMode.GatePairLocked;
    public double BaselineGateScale { get; init; }
    public double LastConfidence { get; init; }
    public double LastBestScore { get; init; }
    public double LastSecondScore { get; init; }
    public double LastCandidateMargin { get; init; }
    public MapStructureRejectionReason LastRejectionReason { get; init; }
    public double LastObservationConfidence { get; init; }
    public double LastObservationBestScore { get; init; }
    public double LastObservationSecondScore { get; init; }
    public double LastObservationCandidateMargin { get; init; }
    public MapStructureRejectionReason LastObservationRejectionReason { get; init; }
    public DateTimeOffset LastObservationAt { get; init; } = DateTimeOffset.UtcNow;
    public int ConsecutiveRejections { get; init; }
    public DateTimeOffset LastSuccessfulAt { get; init; } = DateTimeOffset.UtcNow;
    public bool HasGatePairLock { get; init; } = true;
    public bool LastStructureAttempted { get; init; }
    public bool LastStructureAccepted { get; init; }
    public string LastStructureFailureReason { get; init; } = string.Empty;
    public int ConsecutiveStructureFailures { get; init; }
    public AlignmentSearchStage LastSearchStage { get; init; }
    /// <summary>侧门扫描提供的地图身份先验置信度（0-1）。用于提升后续结构配准的置信度。</summary>
    public double SideEntranceScanPriorConfidence { get; init; }

    public double? GateTemplateScale
    {
        get
        {
            var scales = LockedGateEvidence
                .Select(evidence => evidence.TemplateScale)
                .Where(scale => double.IsFinite(scale) && scale > 0d)
                .ToArray();
            return scales.Length == 0 ? null : scales.Average();
        }
    }

    /// <summary>
    /// Replaces only the uniform scale of a side-entrance seed. The observed
    /// gate remains at the same screen center, so the translation is recomputed
    /// from the existing reference/screen-center pair instead of being copied
    /// from a different resolution.
    /// </summary>
    internal MapAlignmentSession WithUniformScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0.05d)
            throw new ArgumentOutOfRangeException(nameof(scale));

        var transform = LockedTransform;
        var scaledTransform = new MapOverlayTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = transform.ScreenCenterX
                - (transform.ReferenceCenterX * scale),
            OffsetY = transform.ScreenCenterY
                - (transform.ReferenceCenterY * scale),
            ReferenceCenterX = transform.ReferenceCenterX,
            ReferenceCenterY = transform.ReferenceCenterY,
            ScreenCenterX = transform.ScreenCenterX,
            ScreenCenterY = transform.ScreenCenterY,
            ReferenceWidth = transform.ReferenceWidth,
            ReferenceHeight = transform.ReferenceHeight,
            OrientationDegrees = transform.OrientationDegrees,
            AlignmentMode = transform.AlignmentMode,
            MaximumResidualPixels = transform.MaximumResidualPixels,
            UsedDegenerateAxisFallback = transform.UsedDegenerateAxisFallback
        };
        return new MapAlignmentSession
        {
            MapId = MapId,
            MapUpdatedAt = MapUpdatedAt,
            FloorKey = FloorKey,
            LockedTransform = scaledTransform,
            LockedGateEvidence = LockedGateEvidence,
            Mode = Mode,
            BaselineGateScale = scale,
            LastConfidence = LastConfidence,
            LastBestScore = LastBestScore,
            LastSecondScore = LastSecondScore,
            LastCandidateMargin = LastCandidateMargin,
            LastRejectionReason = LastRejectionReason,
            LastObservationConfidence = LastObservationConfidence,
            LastObservationBestScore = LastObservationBestScore,
            LastObservationSecondScore = LastObservationSecondScore,
            LastObservationCandidateMargin = LastObservationCandidateMargin,
            LastObservationRejectionReason = LastObservationRejectionReason,
            LastObservationAt = LastObservationAt,
            ConsecutiveRejections = ConsecutiveRejections,
            LastSuccessfulAt = LastSuccessfulAt,
            HasGatePairLock = HasGatePairLock,
            LastStructureAttempted = LastStructureAttempted,
            LastStructureAccepted = LastStructureAccepted,
            LastStructureFailureReason = LastStructureFailureReason,
            ConsecutiveStructureFailures = ConsecutiveStructureFailures,
            LastSearchStage = LastSearchStage,
            SideEntranceScanPriorConfidence = SideEntranceScanPriorConfidence
        };
    }

    public static MapAlignmentSession FromRecognition(
        MapRecord map,
        MapRecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(result);
        var transform = result.OverlayTransform
            ?? throw new InvalidOperationException("识别结果没有可用的地图对齐变换。");
        var profile = MapFloorRules.GetFloorProfile(map, result.Floor)
            ?? map.Recognition.FirstFloor;
        var requiredIds = profile.RequiredAnchors
            .Select(anchor => anchor.Id)
            .ToHashSet();
        var lockedEvidence = result.AnchorMatches
            .Where(evidence => requiredIds.Contains(evidence.AnchorId))
            .ToArray();
        return new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = result.Floor,
            LockedTransform = transform,
            LockedGateEvidence = lockedEvidence,
            BaselineGateScale = transform.ScaleX,
            LastConfidence = result.Confidence,
            LastBestScore = result.StructureBestScore,
            LastSecondScore = result.StructureSecondScore,
            LastCandidateMargin = result.StructureCandidateMargin,
            LastRejectionReason = result.StructureRejectionReason,
            LastObservationConfidence = result.Confidence,
            LastObservationBestScore = result.StructureBestScore,
            LastObservationSecondScore = result.StructureSecondScore,
            LastObservationCandidateMargin = result.StructureCandidateMargin,
            LastObservationRejectionReason = result.StructureRejectionReason,
            LastObservationAt = DateTimeOffset.UtcNow,
            LastSuccessfulAt = DateTimeOffset.UtcNow,
            SideEntranceScanPriorConfidence = result.Source
                    == MapRecognitionSource.SideEntranceSelection
                ? result.Confidence
                : 0d,
            HasGatePairLock = result.Source
                != MapRecognitionSource.SideEntranceSelection
                && string.Equals(
                result.Floor,
                MapFloorRules.GetPrimaryFloorKey(map),
                StringComparison.Ordinal),
            Mode = result.Source switch
            {
                MapRecognitionSource.SideEntranceSelection =>
                    MapAlignmentTrackingMode.AuxiliaryAnchorTracking,
                MapRecognitionSource.SingleGateTracking => MapAlignmentTrackingMode.SingleGateTracking,
                MapRecognitionSource.AuxiliaryAnchorTracking => MapAlignmentTrackingMode.AuxiliaryAnchorTracking,
                MapRecognitionSource.StructureMatching => MapAlignmentTrackingMode.StructureMatched,
                MapRecognitionSource.OrbTracking => MapAlignmentTrackingMode.OrbTracking,
                _ => MapAlignmentTrackingMode.GatePairLocked
            }
        };
    }

    internal static MapAlignmentSession RebuildPreservingFirstScanIdentity(
        MapAlignmentSession? previous,
        MapRecord map,
        MapRecognitionResult result)
    {
        var rebuilt = FromRecognition(map, result);
        if (previous is null
            || previous.SideEntranceScanPriorConfidence <= 0d
            || rebuilt.SideEntranceScanPriorConfidence > 0d
            || previous.MapId != rebuilt.MapId
            || previous.MapUpdatedAt != rebuilt.MapUpdatedAt
            || !string.Equals(
                previous.FloorKey,
                rebuilt.FloorKey,
                StringComparison.Ordinal))
        {
            return rebuilt;
        }

        return new MapAlignmentSession
        {
            MapId = rebuilt.MapId,
            MapUpdatedAt = rebuilt.MapUpdatedAt,
            FloorKey = rebuilt.FloorKey,
            LockedTransform = rebuilt.LockedTransform,
            LockedGateEvidence = previous.LockedGateEvidence.Count > 0
                ? previous.LockedGateEvidence
                : rebuilt.LockedGateEvidence,
            BaselineGateScale = rebuilt.BaselineGateScale,
            LastConfidence = rebuilt.LastConfidence,
            LastBestScore = rebuilt.LastBestScore,
            LastSecondScore = rebuilt.LastSecondScore,
            LastCandidateMargin = rebuilt.LastCandidateMargin,
            LastRejectionReason = rebuilt.LastRejectionReason,
            LastObservationConfidence = rebuilt.LastObservationConfidence,
            LastObservationBestScore = rebuilt.LastObservationBestScore,
            LastObservationSecondScore = rebuilt.LastObservationSecondScore,
            LastObservationCandidateMargin = rebuilt.LastObservationCandidateMargin,
            LastObservationRejectionReason = rebuilt.LastObservationRejectionReason,
            LastObservationAt = rebuilt.LastObservationAt,
            ConsecutiveRejections = rebuilt.ConsecutiveRejections,
            LastSuccessfulAt = rebuilt.LastSuccessfulAt,
            HasGatePairLock = false,
            SideEntranceScanPriorConfidence =
                previous.SideEntranceScanPriorConfidence,
            Mode = rebuilt.Mode,
            LastStructureAttempted = rebuilt.LastStructureAttempted,
            LastStructureAccepted = rebuilt.LastStructureAccepted,
            LastStructureFailureReason = rebuilt.LastStructureFailureReason,
            ConsecutiveStructureFailures = rebuilt.ConsecutiveStructureFailures,
            LastSearchStage = rebuilt.LastSearchStage,
        };
    }

    public MapAlignmentSession Advance(
        MapRecord map,
        MapRecognitionResult result,
        double maximumScaleChangeRatio =
            MapSessionRules.NativeScaleChangeRatio)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(result);
        if (map.Id != MapId || result.MapId != MapId)
            throw new InvalidOperationException("不能用其他地图的结果更新当前对齐会话。");
        if (result.Source == MapRecognitionSource.ReusedLastTransform)
            return Hold(null);
        if (map.UpdatedAt != MapUpdatedAt
            || !string.Equals(result.Floor, FloorKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A continuous alignment observation cannot cross a map-version or floor change.");
        }
        if (result.Source is not (
                MapRecognitionSource.SingleGateTracking
                or MapRecognitionSource.AuxiliaryAnchorTracking
                or MapRecognitionSource.StructureMatching
                or MapRecognitionSource.OrbTracking))
        {
            throw new InvalidOperationException(
                "Only tracking observations can advance an existing alignment lock.");
        }

        var candidateTransform = result.OverlayTransform
            ?? throw new InvalidOperationException(
                "The tracking observation has no transform.");
        var candidateSimilarity = MapSimilarityTransform.FromOverlay(
            candidateTransform);
        if (!candidateSimilarity.IsValid
            || !double.IsFinite(BaselineGateScale)
            || BaselineGateScale <= 0d)
        {
            throw new InvalidOperationException(
                "The tracking observation transform is invalid.");
        }
        var allowedScaleChange = Math.Clamp(
            double.IsFinite(maximumScaleChangeRatio)
                ? maximumScaleChangeRatio
                : MapSessionRules.NativeScaleChangeRatio,
            0d,
            0.50d);
        var scaleChange = Math.Abs(
            (candidateSimilarity.Scale / BaselineGateScale) - 1d);
        if (scaleChange > allowedScaleChange)
        {
            throw new InvalidOperationException(
                $"The tracking scale changed by {scaleChange:P1}, above the locked limit {allowedScaleChange:P1}.");
        }

        var advancedTransform = result.OverlayTransform
            ?? throw new InvalidOperationException("跟踪结果没有可用的地图对齐变换。");
        // 侧门会话的缩放基线跟随最新结构配准结果。结构配准允许在
        // LockedTransform.ScaleX ± 搜索半径内微调 scale；若 BaselineGateScale
        // 固定在侧门扫描初值，多次 Advance 后会与 LockedTransform.ScaleX 累积
        // 分叉，导致高质量结构配准被误判为"超过安全范围的地图缩放"。
        var advancedBaselineGateScale = SideEntranceScanPriorConfidence > 0d
            ? advancedTransform.ScaleX
            : BaselineGateScale;
        return new MapAlignmentSession
        {
            MapId = MapId,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = result.Floor,
            LockedTransform = advancedTransform,
            LockedGateEvidence = LockedGateEvidence,
            BaselineGateScale = advancedBaselineGateScale,
            LastConfidence = result.Confidence,
            LastBestScore = result.StructureBestScore,
            LastSecondScore = result.StructureSecondScore,
            LastCandidateMargin = result.StructureCandidateMargin,
            LastRejectionReason = result.StructureRejectionReason,
            LastObservationConfidence = result.Confidence,
            LastObservationBestScore = result.StructureBestScore,
            LastObservationSecondScore = result.StructureSecondScore,
            LastObservationCandidateMargin = result.StructureCandidateMargin,
            LastObservationRejectionReason = result.StructureRejectionReason,
            LastObservationAt = DateTimeOffset.UtcNow,
            ConsecutiveRejections = 0,
            LastSuccessfulAt = DateTimeOffset.UtcNow,
            HasGatePairLock = HasGatePairLock,
            SideEntranceScanPriorConfidence = SideEntranceScanPriorConfidence,
            Mode = result.Source switch
            {
                MapRecognitionSource.SingleGateTracking =>
                    MapAlignmentTrackingMode.SingleGateTracking,
                MapRecognitionSource.StructureMatching =>
                    MapAlignmentTrackingMode.StructureMatched,
                MapRecognitionSource.OrbTracking =>
                    MapAlignmentTrackingMode.OrbTracking,
                _ => MapAlignmentTrackingMode.AuxiliaryAnchorTracking
            }
        };
    }

    public MapAlignmentSession AdvanceContinuousObservation(
        MapRecord map,
        MapRecognitionResult result,
        MapSessionSnapshot lockSnapshot,
        MapAlignmentObservationContext observation,
        double maximumScaleChangeRatio =
            MapSessionRules.NativeScaleChangeRatio)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsCurrent(map, this, lockSnapshot))
        {
            throw new InvalidOperationException(
                "The continuous alignment observation belongs to a stale lock revision.");
        }
        return Advance(map, result, maximumScaleChangeRatio);
    }

    /// <summary>
    /// Keeps the last trusted transform after a failed or unavailable
    /// observation. Ordinary holds are not evidence that the lock is wrong,
    /// so they clear the consecutive contradiction streak.
    /// </summary>
    public MapAlignmentSession Hold(MapStructureRegistrationResult? result) =>
        CreateHeldSession(result, consecutiveRejections: 0);

    /// <summary>
    /// Records a rejected continuous-tracking observation. Only a
    /// contradictory result for the exact locked map version and floor is
    /// allowed to advance the lock-loss streak. Inconclusive results and
    /// capture/system failures keep the rendered transform without counting
    /// toward loss.
    /// </summary>
    public MapAlignmentSession HoldContinuousObservation(
        MapRecord map,
        MapSessionSnapshot lockSnapshot,
        MapAlignmentObservationContext observation,
        MapStructureRegistrationResult? result)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(lockSnapshot);
        ArgumentNullException.ThrowIfNull(observation);
        var sameLockIdentity = observation.IsCurrent(
            map,
            this,
            lockSnapshot);
        var isContradictory = result?.RejectionReason
            .ToContinuousLockDisposition()
            == MapStructureEvidenceDisposition.Contradictory;
        return CreateHeldSession(
            result,
            sameLockIdentity && isContradictory
                ? ConsecutiveRejections + 1
                : 0);
    }

    public MapAlignmentObservationContext BeginContinuousObservation(
        MapRecord map,
        MapSessionSnapshot lockSnapshot)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(lockSnapshot);
        var context = new MapAlignmentObservationContext(
            lockSnapshot.AlignmentRevision,
            MapId,
            MapUpdatedAt,
            FloorKey);
        if (!context.IsCurrent(map, this, lockSnapshot))
        {
            throw new InvalidOperationException(
                "A continuous alignment observation requires the current locked map revision.");
        }
        return context;
    }

    private MapAlignmentSession CreateHeldSession(
        MapStructureRegistrationResult? result,
        int consecutiveRejections) => new()
    {
        MapId = MapId,
        MapUpdatedAt = MapUpdatedAt,
        FloorKey = FloorKey,
        LockedTransform = LockedTransform,
        LockedGateEvidence = LockedGateEvidence,
        BaselineGateScale = BaselineGateScale,
        // A rejected observation must not downgrade the confidence or score
        // attached to the transform that is still being rendered.
        LastConfidence = LastConfidence,
        LastBestScore = LastBestScore,
        LastSecondScore = LastSecondScore,
        LastCandidateMargin = LastCandidateMargin,
        LastRejectionReason = LastRejectionReason,
        LastObservationConfidence = result?.Confidence ?? LastObservationConfidence,
        LastObservationBestScore = result?.BestScore ?? LastObservationBestScore,
        LastObservationSecondScore = result?.SecondScore ?? LastObservationSecondScore,
        LastObservationCandidateMargin = result?.CandidateMargin
            ?? LastObservationCandidateMargin,
        LastObservationRejectionReason = result?.RejectionReason
            ?? MapStructureRejectionReason.NoCandidate,
        LastObservationAt = DateTimeOffset.UtcNow,
        ConsecutiveRejections = Math.Max(0, consecutiveRejections),
        LastSuccessfulAt = LastSuccessfulAt,
        HasGatePairLock = HasGatePairLock,
        SideEntranceScanPriorConfidence = SideEntranceScanPriorConfidence,
        Mode = MapAlignmentTrackingMode.HoldingLastTransform,
        LastStructureAttempted = result is not null,
        LastStructureAccepted = result?.Accepted ?? false,
        LastStructureFailureReason = result?.FailureReason ?? string.Empty,
        ConsecutiveStructureFailures =
            result is not null && !result.Accepted
                ? ConsecutiveStructureFailures + 1
                : ConsecutiveStructureFailures,
        LastSearchStage = AlignmentSearchStage.StructureFallback,
    };
}
/*
 * 文件职责：MapAlignmentSession。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
