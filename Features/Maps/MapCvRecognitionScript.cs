using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct MapScreenRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + (Width / 2d);
    public double CenterY => Y + (Height / 2d);
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct MapNormalizedPoint(double X, double Y);

/// <summary>A frozen alignment input frame: viewport pixels plus the capture context.</summary>
public sealed class CapturedGameFrame : IDisposable
{
    public CapturedGameFrame(
        Mat image,
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        IntPtr windowHandle)
    {
        Image = image;
        ClientBounds = clientBounds;
        ViewportBounds = viewportBounds;
        WindowHandle = windowHandle;
    }

    /// <summary>The frozen viewport pixels; calibration captures use the whole client area.</summary>
    public Mat Image { get; }
    public MapScreenRect ClientBounds { get; }
    public MapScreenRect ViewportBounds { get; }
    public IntPtr WindowHandle { get; }

    public void Dispose() => Image.Dispose();
}

public enum MapOverlayAlignmentMode
{
    IndependentAxes,
    Uniform
}

public static class MapOverlayAlignmentModeExtensions
{
    public static string ToDisplayName(this MapOverlayAlignmentMode mode) => mode switch
    {
        MapOverlayAlignmentMode.Uniform => "等比缩放",
        _ => "XY 分别缩放"
    };
}

public sealed class GateDetection
{
    public double Score { get; init; }
    public double Scale { get; init; }
    public MapScreenRect ScreenBounds { get; init; }
}

public enum MapRecognitionSource
{
    Automatic,
    ManualGateSelection,
    UserConfirmed,
    SelectedMapGatePair,
    SingleGateTracking,
    AuxiliaryAnchorTracking,
    StructureMatching,
    ReusedLastTransform
}

public enum MapAlignmentEvidenceKind
{
    None,
    DualGate,
    SingleGateAndAuxiliary,
    AuxiliaryConsensus,
    Structure
}

public static class MapFastAlignmentRules
{
    public const double MinimumDirectLockConfidence = 0.75d;

    public static bool CanDirectLockDualGate(
        MapRecognitionResult result,
        MapRecognitionTuning tuning) =>
        result.HasAllRequiredAnchorEvidence
        && result.AnchorMatches.Count >= 2
        && result.AnchorMatches.All(match =>
            match.Score >= tuning.GateTemplateThreshold)
        && result.Confidence >= MinimumDirectLockConfidence
        && !result.WasForcedBestResult
        && !result.ReusedLastTransform;
}

public enum MapAlignmentTrackingMode
{
    None,
    NeedsGatePair,
    GatePairLocked,
    SingleGateTracking,
    AuxiliaryAnchorTracking,
    WaitingForAnchor,
    StructureMatched,
    HoldingLastTransform,
    Lost,
    Uninitialized = None,
    AnchorCalibrated = GatePairLocked,
    OffsetOnlyUpdated = SingleGateTracking
}

public sealed class CvAnchorEvidence
{
    public Guid AnchorId { get; init; }
    public double Score { get; init; }
    public double TemplateScale { get; init; }
    public MapScreenRect ReferenceBounds { get; init; }
    public MapScreenRect ScreenBounds { get; init; }
}

public sealed class MapOverlayTransform
{
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double ReferenceCenterX { get; init; }
    public double ReferenceCenterY { get; init; }
    public double ScreenCenterX { get; init; }
    public double ScreenCenterY { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public int OrientationDegrees { get; init; }
    public MapOverlayAlignmentMode AlignmentMode { get; init; }
    public double MaximumResidualPixels { get; init; }
    public bool UsedDegenerateAxisFallback { get; init; }
    public bool IsExactFit => MaximumResidualPixels <= MapOverlayTransformSolver.ExactFitTolerancePixels;
}

public sealed class MapRecognitionResult
{
    public Guid MapId { get; init; }
    public string Floor { get; init; } = "1f";
    public int OrientationDegrees { get; init; }
    public double Confidence { get; init; }
    public MapRecognitionSource Source { get; init; } = MapRecognitionSource.Automatic;
    public bool HasAllRequiredAnchorEvidence { get; init; }
    public double GeometryMargin { get; init; }
    public bool UsedLocalConfirmation { get; init; }
    public MapOverlayTransform? OverlayTransform { get; init; }
    public IReadOnlyList<CvAnchorEvidence> AnchorMatches { get; init; } = [];
    public double StructureBestScore { get; init; }
    public double StructureSecondScore { get; init; }
    public double StructureCandidateMargin { get; init; }
    public MapStructureRejectionReason StructureRejectionReason { get; init; }
    public bool WasForcedBestResult { get; init; }
    public bool ReusedLastTransform { get; init; }
    public MapAlignmentEvidenceKind EvidenceKind { get; init; }
    public MapStructureEvidenceDisposition StructureDisposition { get; init; }
    public bool SkippedStructureValidation { get; init; }
}

public enum AlignmentSearchStage
{
    None,
    FullGateSearch,
    WarmGateSearch,
    LockedGateSearch,
    LocalGateConfirmation,
    StructureFallback,
}

public sealed class MapScanDiagnostics
{
    public int ReadyMapCount { get; set; }
    public int TotalMapCount { get; set; }
    public int GateCandidateCount { get; set; }
    public GateSearchMode GateSearchMode { get; set; }
    public GateSearchStopReason GateSearchStopReason { get; set; }
    public int GateScalesEvaluated { get; set; }
    public int GateMatchTemplateCalls { get; set; }
    public bool GateBudgetExceeded { get; set; }
    public AlignmentSearchStage SearchStage { get; set; }
    public int AuxiliaryAnchorMatchCount { get; set; }
    public int AuxiliaryTemplatesEvaluated { get; set; }
    public double AuxiliaryAnchorMilliseconds { get; set; }
    public double AuxiliaryConfidence { get; set; }
    public bool AuxiliaryUsedGlobalSearch { get; set; }
    public bool UsedSingleGateStructureFallback { get; set; }
    public bool UsedForcedBestResult { get; set; }
    public string SingleGateFallbackReason { get; set; } = string.Empty;
    public MapAlignmentTrackingMode TrackingMode { get; set; }
    public string? DetectedFloor { get; set; }
    public double FloorConfidence { get; set; }
    public double FloorCaptureMilliseconds { get; set; }
    public double FloorAnalysisMilliseconds { get; set; }
    public double FloorEndToEndMilliseconds { get; set; }
    public double CaptureMilliseconds { get; set; }
    public double CacheMilliseconds { get; set; }
    public double PreprocessMilliseconds { get; set; }
    public double GateDetectionMilliseconds { get; set; }
    public double GeometryMilliseconds { get; set; }
    public double ConfirmationMilliseconds { get; set; }
    public double StructurePreprocessMilliseconds { get; set; }
    public double StructureSearchMilliseconds { get; set; }
    public double StructureRefineMilliseconds { get; set; }
    public double StructureBestScore { get; set; }
    public double StructureSecondScore { get; set; }
    public double StructureCandidateMargin { get; set; }
    public double StructureGeometricFitQuality { get; set; }
    public double StructureEvidenceConfidence { get; set; }
    public double StructureGeometricLockConfidence { get; set; }
    public double StructureLockConfidence { get; set; }
    public string? StructureLowEvidenceReason { get; set; }
    public string? StructureHardGateFailure { get; set; }
    public MapStructureRejectionReason StructureRejectionReason { get; set; }
    public MapStructureEvidenceDisposition StructureDisposition { get; set; }
    public bool SkippedStructureValidation { get; set; }
    public bool StructureAttempted { get; set; }
    public bool StructureAccepted { get; set; }
    public string StructureFailureReason { get; set; } = string.Empty;
    public MapAlignmentEvidenceKind AlignmentEvidence { get; set; }
    public int StructureCandidateCount { get; set; }
    public int StructureFeatureMatchCount { get; set; }
    public int StructureFeatureInlierCount { get; set; }
    public double StructureFeatureConsensus { get; set; }
    public bool StructureEccConverged { get; set; }
    public double StructureEccCorrelation { get; set; }
    public double OverlayMilliseconds { get; set; }
    public double TotalMilliseconds { get; set; }

    // ── Phase 0: mutually-exclusive wall-clock timing ──

    /// <summary>Input → alignment start (animation wait + stability detection).</summary>
    public double InputToAlignmentStartMilliseconds { get; set; }

    /// <summary>Pure Task.Delay inside HandleGameMapToggleAsync.</summary>
    public double OpeningAnimationWaitMilliseconds { get; set; }

    /// <summary>Wall-clock inside WaitForStableViewportAsync (includes capture + Delay).</summary>
    public double StableViewportWaitMilliseconds { get; set; }

    /// <summary>Cumulative TryCaptureViewport time inside stability loop.</summary>
    public double StableViewportCaptureMilliseconds { get; set; }

    /// <summary>Stability loop attempt / capture counts.</summary>
    public int StableViewportAttempts { get; set; }
    public int StableViewportSuccessfulCaptures { get; set; }

    /// <summary>Floor: request enqueued → worker thread picks up.</summary>
    public double FloorQueueMilliseconds { get; set; }

    /// <summary>Floor: worker pickup → result produced.</summary>
    public double FloorWorkerMilliseconds { get; set; }

    /// <summary>Floor: enqueued → result produced (Queue + Worker).</summary>
    public double FloorRequestMilliseconds { get; set; }

    /// <summary>Floor: input → result (includes animation + stability overhead).</summary>
    public double FloorInputToResultMilliseconds { get; set; }

    /// <summary>Sum of Thread.Sleep / Task.Delay inside floor retry loop.</summary>
    public double FloorRetryWaitMilliseconds { get; set; }

    /// <summary>Worker wall time minus (capture + analysis) — true overhead.</summary>
    public double FloorWorkerOverheadMilliseconds { get; set; }

    /// <summary>Capture inside RunSelectedMapAlignmentAsync (after stability).</summary>
    public double AlignmentCaptureMilliseconds { get; set; }

    /// <summary>Task.Run dispatch overhead for alignment compute.</summary>
    public double AlignmentDispatchMilliseconds { get; set; }

    /// <summary>Cv2.ImRead for reference image (if not cached).</summary>
    public double ReferenceImageLoadMilliseconds { get; set; }

    /// <summary>_structureCache.GetOrCreate wall time.</summary>
    public double ReferenceCacheMilliseconds { get; set; }

    /// <summary>ProcessLiveRoi wall time inside structure registration.</summary>
    public double LiveStructurePreprocessMilliseconds { get; set; }

    /// <summary>ConfirmAlignmentCandidateAsync: pure Task.Delay inside loop.</summary>
    public double ConfirmationDelayMilliseconds { get; set; }

    /// <summary>ConfirmAlignmentCandidateAsync: TryCaptureViewport cumulative.</summary>
    public double ConfirmationCaptureMilliseconds { get; set; }

    /// <summary>ConfirmAlignmentCandidateAsync: Task.Run compute (excl. Delay + Capture).</summary>
    public double ConfirmationComputeMilliseconds { get; set; }

    /// <summary>Session transition + SaveAlignmentCalibrationAsync.</summary>
    public double SessionCommitMilliseconds { get; set; }

    /// <summary>Three target metrics — mutually exclusive.</summary>
    public double FirstCandidateMilliseconds { get; set; }
    public double AlignmentPipelineMilliseconds { get; set; }
    public double InputToLockedMilliseconds { get; set; }
    public double VisibleMaskMs { get; set; }
    public double VisibleFraction { get; set; }
    public int VisibleStructurePixels { get; set; }
    public int VisibleEdgePixels { get; set; }
    public double VisibleAwareSearchMs { get; set; }
    public int VisibleAwareCandidateCount { get; set; }
    public double VisibleAwareTopCost { get; set; }
    public double VisibleAwareTopMargin { get; set; }
    public bool VisibleAwareEarlyAccepted { get; set; }
    public string? VisibleAwareFallbackReason { get; set; }

    // Fast alignment diagnostics
    public bool StructureFastStrategyUsed { get; set; }
    public double StructureCoarseSearchMs { get; set; }
    public int StructureCoarseCandidateCount { get; set; }

    public string ToStatusText() =>
        $"地图 {_ReadyText()}"
        + (DetectedFloor is { } floor
            ? $" · 楼层 {floor.ToUpperInvariant()} {FloorRequestMilliseconds:F1}ms"
                + (FloorRequestMilliseconds
                        > MapFloorRecognitionRules.PerformanceBudgetMilliseconds
                    ? "（超过100ms目标）"
                    : string.Empty)
            : string.Empty)
        + $" · 捕获 {CaptureMilliseconds:F0}ms · 门 {GateDetectionMilliseconds:F0}ms · 排名 {GeometryMilliseconds:F0}ms"
        + (AuxiliaryAnchorMilliseconds > 0d
            ? $" · 辅助锚点 {AuxiliaryAnchorMilliseconds:F0}ms/{AuxiliaryAnchorMatchCount}"
            : string.Empty)
        + (ConfirmationMilliseconds > 0 ? $" · 复核 {ConfirmationMilliseconds:F0}ms" : string.Empty)
        + (UsedSingleGateStructureFallback ? " · 单门复核失败，已回退结构" : string.Empty)
        + (UsedForcedBestResult ? " · 已强制采用最优结果" : string.Empty)
        + (StructureSearchMilliseconds > 0
            ? $" · 结构 {StructurePreprocessMilliseconds + StructureSearchMilliseconds + StructureRefineMilliseconds:F0}ms"
            : string.Empty)
        + (SkippedStructureValidation ? " · 已跳过结构复核" : string.Empty)
        + (StructureRejectionReason != MapStructureRejectionReason.None
            ? $" · 拒绝 {StructureRejectionReason.ToDisplayText()}"
            : string.Empty)
        + $" · 总计 {TotalMilliseconds:F0}ms";

    private string _ReadyText() => $"{ReadyMapCount}/{TotalMapCount} 就绪";
}

/// <summary>Result from WaitForStableViewportAsync with per-capture timing.</summary>
public sealed class StableViewportResult
{
    public bool Succeeded { get; init; }
    public double TotalWaitMs { get; init; }
    public double CaptureMs { get; init; }
    public double DelayMs { get; init; }
    public int Attempts { get; init; }
    public int SuccessfulCaptures { get; init; }
}

/// <summary>Timing data from pre-alignment phase (animation + stability).</summary>
public sealed class MapPreAlignmentTiming
{
    public double InputToAlignmentStartMs { get; init; }
    public double AnimationWaitMs { get; init; }
    public double StableViewportWaitMs { get; init; }
    public double StableViewportCaptureMs { get; init; }
    public double StableViewportDelayMs { get; init; }
    public int StableViewportAttempts { get; init; }
    public int StableViewportSuccessfulCaptures { get; init; }
}

/// <summary>
/// Identifies the exact alignment-lock revision under which an asynchronous
/// continuous observation began. Results from an older revision are stale
/// even when the reopened map and floor have the same identity.
/// </summary>
public sealed record MapAlignmentObservationContext(
    long AlignmentRevision,
    Guid MapId,
    DateTimeOffset MapUpdatedAt,
    string FloorKey)
{
    public bool IsCurrent(
        MapRecord map,
        MapAlignmentSession? alignmentSession,
        MapSessionSnapshot lockSnapshot) =>
        AlignmentRevision > 0
        && lockSnapshot.IsLocked
        && lockSnapshot.AlignmentRevision == AlignmentRevision
        && lockSnapshot.MapId == MapId
        && string.Equals(lockSnapshot.Floor, FloorKey, StringComparison.Ordinal)
        && map.Id == MapId
        && map.UpdatedAt == MapUpdatedAt
        && alignmentSession is not null
        && alignmentSession.MapId == MapId
        && alignmentSession.MapUpdatedAt == MapUpdatedAt
        && string.Equals(
            alignmentSession.FloorKey,
            FloorKey,
            StringComparison.Ordinal);
}

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
            HasGatePairLock = string.Equals(
                result.Floor,
                MapFloorRules.GetPrimaryFloorKey(map),
                StringComparison.Ordinal),
            Mode = result.Source switch
            {
                MapRecognitionSource.SingleGateTracking => MapAlignmentTrackingMode.SingleGateTracking,
                MapRecognitionSource.AuxiliaryAnchorTracking => MapAlignmentTrackingMode.AuxiliaryAnchorTracking,
                MapRecognitionSource.StructureMatching => MapAlignmentTrackingMode.StructureMatched,
                _ => MapAlignmentTrackingMode.GatePairLocked
            }
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
                or MapRecognitionSource.StructureMatching))
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

        return new MapAlignmentSession
        {
            MapId = MapId,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = result.Floor,
            LockedTransform = result.OverlayTransform
                ?? throw new InvalidOperationException("跟踪结果没有可用的地图对齐变换。"),
            LockedGateEvidence = LockedGateEvidence,
            BaselineGateScale = BaselineGateScale,
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
            Mode = result.Source switch
            {
                MapRecognitionSource.SingleGateTracking =>
                    MapAlignmentTrackingMode.SingleGateTracking,
                MapRecognitionSource.StructureMatching =>
                    MapAlignmentTrackingMode.StructureMatched,
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

public sealed class MapGeometryFingerprint
{
    public MapRecord Map { get; init; } = new();
    public string FloorKey { get; init; } = "1f";
    public MapNormalizedPoint MainPoint { get; init; }
    public MapNormalizedPoint SidePoint { get; init; }
    public MapScreenRect MainReferenceBounds { get; init; }
    public MapScreenRect SideReferenceBounds { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public string RecognitionImagePath { get; init; } = string.Empty;
    public string OverlayImagePath { get; init; } = string.Empty;

    /// <summary>
    /// Actual gate icon width in the reference image, measured by template
    /// matching. Zero means "not measured" and the system falls back to
    /// <see cref="MainReferenceBounds"/> / <see cref="SideReferenceBounds"/>.
    /// </summary>
    public double ReferenceGateIconWidth { get; init; }
    /// <summary>Actual gate icon height in the reference image (see <see cref="ReferenceGateIconWidth"/>).</summary>
    public double ReferenceGateIconHeight { get; init; }
    /// <summary>True when both icon dimensions have been measured.</summary>
    public bool HasReferenceGateIconSize =>
        ReferenceGateIconWidth > 0d && ReferenceGateIconHeight > 0d;

    public double DeltaX => SidePoint.X - MainPoint.X;
    public double DeltaY => SidePoint.Y - MainPoint.Y;
    public double Distance => Math.Sqrt((DeltaX * DeltaX) + (DeltaY * DeltaY));
    public double Angle => Math.Atan2(DeltaY, DeltaX);
}

public sealed class MapGeometryCandidate
{
    public MapGeometryFingerprint Fingerprint { get; init; } = new();
    public GateDetection MainGate { get; init; } = new();
    public GateDetection SideGate { get; init; } = new();
    public MapNormalizedPoint ReferenceCenter { get; init; }
    public MapNormalizedPoint ScreenCenter { get; init; }
    public double EstimatedScaleX { get; init; }
    public double EstimatedScaleY { get; init; }
    public double VectorError { get; init; }
    public double DistanceError { get; init; }
    public double AngleError { get; init; }
    public double Score { get; init; }
    public double ConfirmationScore { get; set; }
}

/// <summary>Solves an axis-aligned scale and translation from the two identified gate centers.</summary>
public static class MapOverlayTransformSolver
{
    public const double ExactFitTolerancePixels = 2d;
    private const double MinimumScale = 0.1d;
    private const double MaximumScale = 8d;
    private const double MinimumStableAxisPixels = 4d;
    private const double StableAxisDistanceRatio = 0.05d;

    public static bool TrySolve(
        MapGeometryCandidate candidate,
        MapOverlayAlignmentMode mode,
        out MapOverlayTransform transform,
        out string failureReason)
    {
        transform = new MapOverlayTransform();
        failureReason = string.Empty;
        if (!Enum.IsDefined(mode))
        {
            failureReason = "未知的图层对齐模式。";
            return false;
        }

        var fingerprint = candidate.Fingerprint;
        if (fingerprint.ReferenceWidth <= 0 || fingerprint.ReferenceHeight <= 0)
        {
            failureReason = "参考地图裁切尺寸无效。";
            return false;
        }

        var referenceMain = new MapNormalizedPoint(
            fingerprint.MainPoint.X * fingerprint.ReferenceWidth,
            fingerprint.MainPoint.Y * fingerprint.ReferenceHeight);
        var referenceSide = new MapNormalizedPoint(
            fingerprint.SidePoint.X * fingerprint.ReferenceWidth,
            fingerprint.SidePoint.Y * fingerprint.ReferenceHeight);
        var screenMain = new MapNormalizedPoint(
            candidate.MainGate.ScreenBounds.CenterX,
            candidate.MainGate.ScreenBounds.CenterY);
        var screenSide = new MapNormalizedPoint(
            candidate.SideGate.ScreenBounds.CenterX,
            candidate.SideGate.ScreenBounds.CenterY);
        var referenceDeltaX = referenceSide.X - referenceMain.X;
        var referenceDeltaY = referenceSide.Y - referenceMain.Y;
        var screenDeltaX = screenSide.X - screenMain.X;
        var screenDeltaY = screenSide.Y - screenMain.Y;
        var referenceDistance = Length(referenceDeltaX, referenceDeltaY);
        var screenDistance = Length(screenDeltaX, screenDeltaY);
        if (referenceDistance <= 1d || screenDistance <= 1d)
        {
            failureReason = "两个门点距离过小，无法计算图层缩放。";
            return false;
        }

        double scaleX;
        double scaleY;
        var usedFallback = false;
        if (mode == MapOverlayAlignmentMode.Uniform)
        {
            var denominator = (referenceDeltaX * referenceDeltaX) + (referenceDeltaY * referenceDeltaY);
            var uniformScale = (
                (referenceDeltaX * screenDeltaX)
                + (referenceDeltaY * screenDeltaY)) / denominator;
            if (!IsValidScale(uniformScale))
            {
                failureReason = "双门方向或等比缩放倍率无效。";
                return false;
            }
            scaleX = uniformScale;
            scaleY = uniformScale;
        }
        else
        {
            var stableThreshold = Math.Max(
                MinimumStableAxisPixels,
                referenceDistance * StableAxisDistanceRatio);
            var xIsStable = Math.Abs(referenceDeltaX) >= stableThreshold;
            var yIsStable = Math.Abs(referenceDeltaY) >= stableThreshold;
            if (!xIsStable && !yIsStable)
            {
                failureReason = "双门向量没有可用于缩放的稳定轴。";
                return false;
            }

            double? solvedX = xIsStable ? screenDeltaX / referenceDeltaX : null;
            double? solvedY = yIsStable ? screenDeltaY / referenceDeltaY : null;
            if (solvedX is { } x && !IsValidScale(x))
            {
                failureReason = "横向缩放会产生镜像或异常倍率。";
                return false;
            }
            if (solvedY is { } y && !IsValidScale(y))
            {
                failureReason = "纵向缩放会产生镜像或异常倍率。";
                return false;
            }

            usedFallback = solvedX is null || solvedY is null;
            scaleX = solvedX ?? solvedY!.Value;
            scaleY = solvedY ?? solvedX!.Value;
        }

        // The live gate midpoint is the map's runtime center. It moves with
        // map panning and is therefore a safer origin than the calibrated ROI.
        var referenceCenter = Midpoint(referenceMain, referenceSide);
        var screenCenter = Midpoint(screenMain, screenSide);
        var offsetX = screenCenter.X - (referenceCenter.X * scaleX);
        var offsetY = screenCenter.Y - (referenceCenter.Y * scaleY);
        var mainResidual = Length(
            ((referenceMain.X * scaleX) + offsetX) - screenMain.X,
            ((referenceMain.Y * scaleY) + offsetY) - screenMain.Y);
        var sideResidual = Length(
            ((referenceSide.X * scaleX) + offsetX) - screenSide.X,
            ((referenceSide.Y * scaleY) + offsetY) - screenSide.Y);
        var maximumResidual = Math.Max(mainResidual, sideResidual);
        if (!double.IsFinite(offsetX)
            || !double.IsFinite(offsetY)
            || !double.IsFinite(maximumResidual))
        {
            failureReason = "图层缩放或位移计算产生了无效结果。";
            return false;
        }

        transform = new MapOverlayTransform
        {
            ScaleX = scaleX,
            ScaleY = scaleY,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = referenceCenter.X,
            ReferenceCenterY = referenceCenter.Y,
            ScreenCenterX = screenCenter.X,
            ScreenCenterY = screenCenter.Y,
            ReferenceWidth = fingerprint.ReferenceWidth,
            ReferenceHeight = fingerprint.ReferenceHeight,
            OrientationDegrees = 0,
            AlignmentMode = mode,
            MaximumResidualPixels = maximumResidual,
            UsedDegenerateAxisFallback = usedFallback
        };
        return true;
    }

    public static bool TryTranslateWithLockedScale(
        MapOverlayTransform locked,
        IReadOnlyList<CvAnchorEvidence> matches,
        out MapOverlayTransform transform,
        out string failureReason)
    {
        transform = new MapOverlayTransform();
        failureReason = string.Empty;
        if (matches.Count == 0)
        {
            failureReason = "没有可用于更新平移的锚点。";
            return false;
        }
        if (!IsValidScale(locked.ScaleX)
            || !IsValidScale(locked.ScaleY)
            || locked.ReferenceWidth <= 0
            || locked.ReferenceHeight <= 0)
        {
            failureReason = "锁定的地图缩放无效，需要双门重新对齐。";
            return false;
        }

        var weighted = matches
            .Select(match => new
            {
                Match = match,
                Weight = Math.Max(0.0001d, match.Score),
                OffsetX = match.ScreenBounds.CenterX
                    - (match.ReferenceBounds.CenterX * locked.ScaleX),
                OffsetY = match.ScreenBounds.CenterY
                    - (match.ReferenceBounds.CenterY * locked.ScaleY)
            })
            .ToArray();
        var totalWeight = weighted.Sum(item => item.Weight);
        var offsetX = weighted.Sum(item => item.OffsetX * item.Weight) / totalWeight;
        var offsetY = weighted.Sum(item => item.OffsetY * item.Weight) / totalWeight;
        var maximumResidual = weighted.Max(item => Length(
            ((item.Match.ReferenceBounds.CenterX * locked.ScaleX) + offsetX)
                - item.Match.ScreenBounds.CenterX,
            ((item.Match.ReferenceBounds.CenterY * locked.ScaleY) + offsetY)
                - item.Match.ScreenBounds.CenterY));
        if (!double.IsFinite(offsetX)
            || !double.IsFinite(offsetY)
            || !double.IsFinite(maximumResidual))
        {
            failureReason = "锚点平移计算产生了无效结果。";
            return false;
        }

        var referenceCenterX = weighted.Sum(
            item => item.Match.ReferenceBounds.CenterX * item.Weight) / totalWeight;
        var referenceCenterY = weighted.Sum(
            item => item.Match.ReferenceBounds.CenterY * item.Weight) / totalWeight;
        var screenCenterX = weighted.Sum(
            item => item.Match.ScreenBounds.CenterX * item.Weight) / totalWeight;
        var screenCenterY = weighted.Sum(
            item => item.Match.ScreenBounds.CenterY * item.Weight) / totalWeight;
        transform = new MapOverlayTransform
        {
            ScaleX = locked.ScaleX,
            ScaleY = locked.ScaleY,
            OffsetX = offsetX,
            OffsetY = offsetY,
            ReferenceCenterX = referenceCenterX,
            ReferenceCenterY = referenceCenterY,
            ScreenCenterX = screenCenterX,
            ScreenCenterY = screenCenterY,
            ReferenceWidth = locked.ReferenceWidth,
            ReferenceHeight = locked.ReferenceHeight,
            OrientationDegrees = locked.OrientationDegrees,
            AlignmentMode = locked.AlignmentMode,
            MaximumResidualPixels = maximumResidual,
            UsedDegenerateAxisFallback = locked.UsedDegenerateAxisFallback
        };
        return true;
    }

    private static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale is >= MinimumScale and <= MaximumScale;

    private static MapNormalizedPoint Midpoint(
        MapNormalizedPoint left,
        MapNormalizedPoint right) =>
        new((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);

    private static double Length(double x, double y) => Math.Sqrt((x * x) + (y * y));
}

/// <summary>Pure geometry ranking used by the runtime recognizer and deterministic tests.</summary>
public static class MapCvRecognitionScript
{
    public const double VectorErrorTolerance =
        MapRecognitionTuning.DefaultVectorErrorTolerance;
    public const double AmbiguityMargin =
        MapRecognitionTuning.DefaultAmbiguityMargin;
    public const double ConfirmationMargin =
        MapRecognitionTuning.DefaultConfirmationAdvantage;

    // ── Dual-gate confidence weighting ─────────────────────────────────
    // Gate template score is the primary evidence: clearly visible gates
    // are the strongest signal that the map is open and identified.
    // Geometry acts as a soft secondary check rather than the dominant term.
    [Obsolete("Use MapAlignmentConfidence.ComputeDualGateConfidence instead")]
    public const double GateScoreConfidenceWeight = 0.50d;
    [Obsolete("Use MapAlignmentConfidence.ComputeDualGateConfidence instead")]
    public const double GeometryConfidenceWeight = 0.50d;
    /// <summary>Soft-decay rate for the geometry goodness curve exp(−k·v/t).</summary>
    public const double GeometryGoodnessDecayRate = 1.0d;

    public static IReadOnlyList<MapGeometryCandidate> RankGeometry(
        IReadOnlyList<MapGeometryFingerprint> fingerprints,
        IReadOnlyList<GateDetection> gates,
        MapScreenRect viewportBounds,
        double vectorErrorTolerance = VectorErrorTolerance,
        bool testSwappedAssignments = true)
    {
        if (fingerprints.Count == 0 || gates.Count < 2 || !viewportBounds.IsValid)
            return [];

        var bestByMap = new Dictionary<Guid, MapGeometryCandidate>();
        for (var left = 0; left < gates.Count - 1; left++)
        {
            for (var right = left + 1; right < gates.Count; right++)
            {
                foreach (var fingerprint in fingerprints)
                {
                    EvaluateAssignment(
                        fingerprint,
                        gates[left],
                        gates[right],
                        vectorErrorTolerance,
                        bestByMap);
                    if (testSwappedAssignments)
                    {
                        EvaluateAssignment(
                            fingerprint,
                            gates[right],
                            gates[left],
                            vectorErrorTolerance,
                            bestByMap);
                    }
                }
            }
        }

        return bestByMap.Values
            .OrderBy(candidate => candidate.VectorError)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Fingerprint.Map.Id)
            .ToArray();
    }

    /// <summary>
    /// Converts a geometry error ratio into a 0..1 "goodness" score using a
    /// soft exponential curve. A match comfortably inside the tolerance keeps
    /// most of its credit; only near-tolerance errors are discounted sharply.
    /// This replaces the former linear (1 − v/t) penalty, which over-penalized
    /// matches that were already well within the tolerance.
    /// </summary>
    public static double GeometryGoodness(
        double vectorError,
        double vectorErrorTolerance) =>
        MapAlignmentConfidence.GeometryGoodness(
            vectorError,
            vectorErrorTolerance);

    /// <summary>
    /// Confidence for a dual-gate recognition. Gate template score carries the
    /// primary weight — clearly visible gates are the strongest evidence —
    /// while geometry contributes as a soft secondary check.
    /// </summary>
    [Obsolete("Use MapAlignmentConfidence.ComputeDualGateConfidence instead")]
    public static double ComputeDualGateConfidence(
        double mainGateScore,
        double sideGateScore,
        double vectorError,
        double vectorErrorTolerance) =>
        MapAlignmentConfidence.ComputeDualGateConfidence(
            mainGateScore,
            sideGateScore,
            vectorError,
            vectorErrorTolerance);

    /// <summary>
    /// 单门跟踪的置信度计算。缺少双门几何验证，但可以通过锁定会话的
    /// 先验置信度和模板匹配质量来补偿。单门跟踪本质上是"已知地图+
    /// 锁定缩放"下的平移更新，其可靠性应接近双门对齐。
    /// </summary>
    /// <param name="gateScore">单个门的模板匹配分数</param>
    /// <param name="lockedSessionConfidence">锁定会话的原始置信度（来自初始双门对齐）</param>
    /// <param name="trackingWeight">跟踪模式下当前观测的权重（0.6 = 当前观测60%，先验40%）</param>
    [Obsolete("Use MapAlignmentConfidence.ComputeSingleGateTrackingConfidence with explicit scaleAgreement")]
    public static double ComputeSingleGateTrackingConfidence(
        double gateScore,
        double lockedSessionConfidence,
        double trackingWeight = 0.6d) =>
        MapAlignmentConfidence.ComputeSingleGateTrackingConfidence(
            gateScore,
            lockedSessionConfidence,
            scaleAgreement: 1d); // Legacy: assume perfect scale agreement

    public static double WrappedAngleDifference(double left, double right)
    {
        var difference = Math.Abs(left - right) % (Math.PI * 2d);
        return difference > Math.PI ? (Math.PI * 2d) - difference : difference;
    }

    private static void EvaluateAssignment(
        MapGeometryFingerprint fingerprint,
        GateDetection main,
        GateDetection side,
        double vectorErrorTolerance,
        Dictionary<Guid, MapGeometryCandidate> bestByMap)
    {
        var referenceMain = new MapNormalizedPoint(
            fingerprint.MainPoint.X * fingerprint.ReferenceWidth,
            fingerprint.MainPoint.Y * fingerprint.ReferenceHeight);
        var referenceSide = new MapNormalizedPoint(
            fingerprint.SidePoint.X * fingerprint.ReferenceWidth,
            fingerprint.SidePoint.Y * fingerprint.ReferenceHeight);
        var screenMain = new MapNormalizedPoint(
            main.ScreenBounds.CenterX,
            main.ScreenBounds.CenterY);
        var screenSide = new MapNormalizedPoint(
            side.ScreenBounds.CenterX,
            side.ScreenBounds.CenterY);
        var referenceCenter = Midpoint(referenceMain, referenceSide);
        var screenCenter = Midpoint(screenMain, screenSide);
        var referenceDeltaX = referenceSide.X - referenceMain.X;
        var referenceDeltaY = referenceSide.Y - referenceMain.Y;
        var screenDeltaX = screenSide.X - screenMain.X;
        var screenDeltaY = screenSide.Y - screenMain.Y;

        // Gate boxes scale together with the draggable map. Normalizing each
        // axis by their observed size removes both zoom and ROI dimensions.
        // Use template-matched reference icon sizes when available; otherwise
        // fall back to user-drawn anchor bounds (loose rectangles that may be
        // larger than the actual gate icon).
        var refMainWidth = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconWidth
            : fingerprint.MainReferenceBounds.Width;
        var refSideWidth = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconWidth
            : fingerprint.SideReferenceBounds.Width;
        var refMainHeight = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconHeight
            : fingerprint.MainReferenceBounds.Height;
        var refSideHeight = fingerprint.HasReferenceGateIconSize
            ? fingerprint.ReferenceGateIconHeight
            : fingerprint.SideReferenceBounds.Height;

        var estimatedScaleX = EstimateAxisScale(
            refMainWidth,
            refSideWidth,
            main.ScreenBounds.Width,
            side.ScreenBounds.Width,
            referenceDeltaX,
            screenDeltaX);
        var estimatedScaleY = EstimateAxisScale(
            refMainHeight,
            refSideHeight,
            main.ScreenBounds.Height,
            side.ScreenBounds.Height,
            referenceDeltaY,
            screenDeltaY);
        var alignedReferenceDeltaX = referenceDeltaX * estimatedScaleX;
        var alignedReferenceDeltaY = referenceDeltaY * estimatedScaleY;
        var screenDistance = Length(screenDeltaX, screenDeltaY);
        var alignedReferenceDistance = Length(
            alignedReferenceDeltaX,
            alignedReferenceDeltaY);
        var normalizationDistance = Math.Max(
            1d,
            Math.Max(screenDistance, alignedReferenceDistance));
        var distanceError =
            Math.Abs(screenDistance - alignedReferenceDistance) / normalizationDistance;
        var rawAngleError = WrappedAngleDifference(
            Math.Atan2(screenDeltaY, screenDeltaX),
            Math.Atan2(referenceDeltaY, referenceDeltaX));
        var scaleAdjustedAngleError = WrappedAngleDifference(
            Math.Atan2(screenDeltaY, screenDeltaX),
            Math.Atan2(alignedReferenceDeltaY, alignedReferenceDeltaX));
        var angleError = Math.Min(rawAngleError, scaleAdjustedAngleError);
        var directionError = 2d * Math.Sin(angleError / 2d);
        // Combine direction error and distance error into a single vector
        // metric so that candidates with correct direction but wrong gate
        // spacing cannot pass. Both components are scale-invariant (distance
        // error is already normalised by the longer diagonal) and equally
        // important for rejecting incorrect map identities.
        var vectorError = Math.Sqrt(
            (directionError * directionError)
            + (distanceError * distanceError));
        var tolerance = double.IsFinite(vectorErrorTolerance) && vectorErrorTolerance > 0d
            ? vectorErrorTolerance
            : VectorErrorTolerance;
        var vectorScore = 1d - Math.Clamp(vectorError / tolerance, 0d, 1d);
        var distanceScore = 1d - Math.Clamp(distanceError / tolerance, 0d, 1d);
        var angleScore = 1d - Math.Clamp(angleError / (Math.PI / 12d), 0d, 1d);
        var geometryScore = (vectorScore * 0.65d) + (distanceScore * 0.25d) + (angleScore * 0.10d);
        var templateScore = Math.Clamp((main.Score + side.Score) / 2d, 0d, 1d);
        var candidate = new MapGeometryCandidate
        {
            Fingerprint = fingerprint,
            MainGate = main,
            SideGate = side,
            ReferenceCenter = referenceCenter,
            ScreenCenter = screenCenter,
            EstimatedScaleX = estimatedScaleX,
            EstimatedScaleY = estimatedScaleY,
            VectorError = vectorError,
            DistanceError = distanceError,
            AngleError = angleError,
            Score = (geometryScore * 0.85d) + (templateScore * 0.15d)
        };
        if (!bestByMap.TryGetValue(fingerprint.Map.Id, out var current)
            || candidate.VectorError < current.VectorError
            || (Math.Abs(candidate.VectorError - current.VectorError) < 0.000001d && candidate.Score > current.Score))
        {
            bestByMap[fingerprint.Map.Id] = candidate;
        }
    }

    private static double EstimateAxisScale(
        double firstReferenceSize,
        double secondReferenceSize,
        double firstScreenSize,
        double secondScreenSize,
        double referenceDelta,
        double screenDelta)
    {
        var referenceSize = AveragePositive(firstReferenceSize, secondReferenceSize);
        var screenSize = AveragePositive(firstScreenSize, secondScreenSize);
        if (referenceSize > 0d && screenSize > 0d)
            return screenSize / referenceSize;
        if (Math.Abs(referenceDelta) > 1d)
            return Math.Abs(screenDelta / referenceDelta);
        return 1d;
    }

    private static double AveragePositive(double first, double second)
    {
        var firstIsValid = double.IsFinite(first) && first > 0d;
        var secondIsValid = double.IsFinite(second) && second > 0d;
        if (firstIsValid && secondIsValid)
            return (first + second) / 2d;
        if (firstIsValid)
            return first;
        return secondIsValid ? second : 0d;
    }

    private static MapNormalizedPoint Midpoint(
        MapNormalizedPoint left,
        MapNormalizedPoint right) =>
        new((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);

    private static double Length(double x, double y) => Math.Sqrt((x * x) + (y * y));
}

/// <summary>
/// Creates a weak cross-floor scale seed from reference dimensions.  Screen
/// translation is deliberately discarded and must be solved on the target.
/// </summary>
internal static class MapFloorScaleSeedRules
{
    // Reference dimensions only describe pixel density when both axes changed
    // by roughly the same ratio.  A large disagreement means that the floors
    // simply have different world extents/aspect ratios; averaging those two
    // values produces a fictitious scale (for example 0.94 and 1.82 -> 1.38).
    internal const double MaximumDimensionRatioDisagreement = 0.12d;

    public static double ResolveReferenceScaleRatio(
        FloorRecognitionProfile sourceFloor,
        FloorRecognitionProfile targetFloor,
        out bool usedDimensionRatio)
    {
        var sourceWidth = Math.Max(1, sourceFloor.RecognitionPixelWidth);
        var targetWidth = Math.Max(1, targetFloor.RecognitionPixelWidth);
        var sourceHeight = Math.Max(1, sourceFloor.RecognitionPixelHeight);
        var targetHeight = Math.Max(1, targetFloor.RecognitionPixelHeight);
        var widthRatio = (double)sourceWidth / targetWidth;
        var heightRatio = (double)sourceHeight / targetHeight;
        var disagreement = Math.Abs(widthRatio - heightRatio)
            / Math.Max(widthRatio, heightRatio);
        usedDimensionRatio = double.IsFinite(disagreement)
            && disagreement <= MaximumDimensionRatioDisagreement;
        if (usedDimensionRatio)
            return (widthRatio + heightRatio) / 2d;

        // 宽高比不一致时回退到对角线长度比，而非直接假设 1.0
        //（两层等缩放）。对角线比是对两个维度像素密度的几何综合，
        //即使两层参考图的长宽比差异较大也能给出合理的缩放估计。
        //直接回退到 1.0 会导致基线缩放严重偏小，触发
        //QueryLargerThanReference 拒绝。
        usedDimensionRatio = true;
        var sourceDiagonal = Math.Sqrt(
            (sourceWidth * sourceWidth) + (sourceHeight * sourceHeight));
        var targetDiagonal = Math.Sqrt(
            (targetWidth * targetWidth) + (targetHeight * targetHeight));
        return sourceDiagonal / Math.Max(1d, targetDiagonal);
    }

    public static MapOverlayTransform RenormalizeTransformToFloor(
        MapOverlayTransform source,
        FloorRecognitionProfile sourceFloor,
        FloorRecognitionProfile targetFloor)
    {
        var similarity = MapSimilarityTransform.FromOverlay(source);
        var scale = similarity.Scale * ResolveReferenceScaleRatio(
            sourceFloor,
            targetFloor,
            out _);
        var referenceWidth = Math.Max(1, targetFloor.RecognitionPixelWidth);
        var referenceHeight = Math.Max(1, targetFloor.RecognitionPixelHeight);
        var targetSimilarity = new MapSimilarityTransform
        {
            Scale = scale,
            RotationDegrees = targetFloor.OrientationDegrees,
            TranslationX = 0d,
            TranslationY = 0d
        };
        var center = targetSimilarity.ToScreen(
            new MapReferencePoint(
                referenceWidth / 2d,
                referenceHeight / 2d));
        return new MapOverlayTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceCenterX = referenceWidth / 2d,
            ReferenceCenterY = referenceHeight / 2d,
            ScreenCenterX = center.X,
            ScreenCenterY = center.Y,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            OrientationDegrees = targetFloor.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = source.MaximumResidualPixels
        };
    }
}
