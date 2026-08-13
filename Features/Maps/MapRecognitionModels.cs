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
    private readonly object _derivedFeaturesGate = new();
    private MapStructureFeatures? _defaultLiveStructureFeatures;
    private PreprocessTiming? _defaultLiveStructureTiming;
    private MapStructurePreprocessingProfile _defaultLiveStructureProfile;
    private double _defaultLiveStructureExtractionMilliseconds;
    private bool _disposed;

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

    /// <summary>
    /// Extracts the immutable live structure once for this frozen frame.
    /// Structure retries, expanded searches and scale recovery all operate on
    /// the same pixels, so recomputing AKAZE and connected components for each
    /// attempt is both wasteful and capable of dominating alignment latency.
    /// </summary>
    internal MapStructureFeatures GetOrCreateDefaultLiveStructureFeatures(
        MapStructurePreprocessor preprocessor,
        out bool cacheHit,
        out double extractionMilliseconds,
        out PreprocessTiming timing) =>
        GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            MapStructurePreprocessingProfile.EdgesAndFeatures,
            out cacheHit,
            out extractionMilliseconds,
            out timing);

    internal MapStructureFeatures GetOrCreateDefaultLiveStructureFeatures(
        MapStructurePreprocessor preprocessor,
        MapStructurePreprocessingProfile requestedProfile,
        out bool cacheHit,
        out double extractionMilliseconds,
        out PreprocessTiming timing)
    {
        ArgumentNullException.ThrowIfNull(preprocessor);
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_defaultLiveStructureFeatures is { } existing
                && _defaultLiveStructureTiming is { } existingTiming)
            {
                if (_defaultLiveStructureProfile.Satisfies(requestedProfile))
                {
                    cacheHit = true;
                    extractionMilliseconds =
                        _defaultLiveStructureExtractionMilliseconds;
                    timing = existingTiming;
                    return existing;
                }

                var upgradeStopwatch =
                    System.Diagnostics.Stopwatch.StartNew();
                var upgraded = MapStructurePreprocessor
                    .UpgradeLiveRoiWithDescriptors(
                        existing,
                        out var upgradedTiming);
                upgradeStopwatch.Stop();
                _defaultLiveStructureFeatures = upgraded;
                _defaultLiveStructureTiming = upgradedTiming;
                _defaultLiveStructureProfile =
                    MapStructurePreprocessingProfile.EdgesAndFeatures;
                _defaultLiveStructureExtractionMilliseconds =
                    upgradeStopwatch.Elapsed.TotalMilliseconds;
                existing.Dispose();
                cacheHit = false;
                extractionMilliseconds =
                    _defaultLiveStructureExtractionMilliseconds;
                timing = upgradedTiming;
                return upgraded;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var created = preprocessor.ProcessLiveRoiDiagnostic(
                Image,
                out var createdTiming,
                requestedProfile);
            stopwatch.Stop();
            _defaultLiveStructureFeatures = created;
            _defaultLiveStructureTiming = createdTiming;
            _defaultLiveStructureProfile = requestedProfile;
            _defaultLiveStructureExtractionMilliseconds =
                stopwatch.Elapsed.TotalMilliseconds;
            cacheHit = false;
            extractionMilliseconds =
                _defaultLiveStructureExtractionMilliseconds;
            timing = createdTiming;
            return created;
        }
    }

    public void Dispose()
    {
        lock (_derivedFeaturesGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _defaultLiveStructureFeatures?.Dispose();
            _defaultLiveStructureFeatures = null;
            _defaultLiveStructureTiming = null;
            Image.Dispose();
        }
    }
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
    ReusedLastTransform,
    SideEntranceSelection,
    OrbTracking
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
    /// <summary>最低置信度阈值：双门对齐必须 >= 此值才能直接锁定。</summary>
    public static double MinimumDirectLockConfidence =>
        RecognitionConfigRules.MinimumDirectLockConfidence;

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
    OrbTracking,
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
    private double? _identityConfidence;
    private double? _localizationConfidence;

    public Guid MapId { get; init; }
    public string Floor { get; init; } = "1f";
    public int OrientationDegrees { get; init; }
    /// <summary>
    /// Compatibility confidence used by existing callers.  New recognition
    /// code treats this as the localization confidence so an identity prior
    /// cannot make a geometrically weak alignment look reliable.
    /// </summary>
    public double Confidence { get; init; }
    /// <summary>
    /// Confidence that the selected map identity is correct.  Results created
    /// by legacy callers inherit <see cref="Confidence"/>.
    /// </summary>
    public double IdentityConfidence
    {
        get => _identityConfidence ?? Confidence;
        init => _identityConfidence = value;
    }
    /// <summary>
    /// Confidence that the reported transform is geometrically correct.
    /// Results created by legacy callers inherit <see cref="Confidence"/>.
    /// </summary>
    public double LocalizationConfidence
    {
        get => _localizationConfidence ?? Confidence;
        init => _localizationConfidence = value;
    }
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
    public bool UsedCachedScale { get; init; }
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
    public int SideEntranceReadyMapCount { get; set; }
    public int SideEntranceEligibleMapCount { get; set; }
    public int SideEntranceRejectedCandidateCount { get; set; }
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
    public double IdentityConfidence { get; set; }
    public double LocalizationConfidence { get; set; }
    public int StructureCandidateCount { get; set; }
    public int StructureFeatureMatchCount { get; set; }
    public int StructureFeatureInlierCount { get; set; }
    public double StructureFeatureConsensus { get; set; }
    public bool ScaleBootstrapAttempted { get; set; }
    public bool ScaleBootstrapSucceeded { get; set; }
    public bool ScaleBootstrapValidated { get; set; }
    public double ScaleBootstrapScale { get; set; }
    public double ScaleBootstrapConfidence { get; set; }
    public int ScaleBootstrapUniqueMatches { get; set; }
    public int ScaleBootstrapPairVotes { get; set; }
    public double ScaleBootstrapResidualPixels { get; set; }
    public double ScaleBootstrapRelativeMad { get; set; }
    public string ScaleSeedSource { get; set; } = string.Empty;
    public string ScaleSeedCacheSource { get; set; } = string.Empty;
    public double ScaleSeedScale { get; set; }
    public bool ScaleSeedProjected { get; set; }
    public int ScaleSeedSourceViewportWidth { get; set; }
    public int ScaleSeedSourceViewportHeight { get; set; }
    public int ScaleSeedTargetViewportWidth { get; set; }
    public int ScaleSeedTargetViewportHeight { get; set; }
    public double ProjectedScale { get; set; }
    public double FinalValidatedScale { get; set; }
    public string ScaleSeedRejectionReason { get; set; } = string.Empty;
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
        + (SideEntranceEligibleMapCount > 0
            ? $" · 侧门就绪 {SideEntranceReadyMapCount}/{SideEntranceEligibleMapCount}"
                + $" · 拒绝 {SideEntranceRejectedCandidateCount}"
            : string.Empty)
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
/// Creates a neutral scale seed for one exact floor. Scale evidence from any
/// other floor is deliberately excluded; same-floor sessions and caches may
/// replace this seed later in the alignment pipeline.
/// </summary>
internal static class MapFloorScaleSeedRules
{
    public static MapOverlayTransform CreateIndependentFloorSeed(
        MapRecord map,
        string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? throw new InvalidOperationException(
                $"地图不包含楼层 '{floorKey}'。");
        var width = Math.Max(1, profile.RecognitionPixelWidth);
        var height = Math.Max(1, profile.RecognitionPixelHeight);
        return new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceCenterX = width / 2d,
            ReferenceCenterY = height / 2d,
            ScreenCenterX = width / 2d,
            ScreenCenterY = height / 2d,
            ReferenceWidth = width,
            ReferenceHeight = height,
            OrientationDegrees = profile.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
    }
}
