using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public readonly record struct MapScreenRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + (Width / 2d);
    public double CenterY => Y + (Height / 2d);
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct MapNormalizedPoint(double X, double Y);

public sealed partial class CapturedGameFrame : IDisposable
{
    private readonly object _derivedFeaturesGate = new();
    private MapStructureFeatures? _defaultLiveStructureFeatures;
    private PreprocessTiming? _defaultLiveStructureTiming;
    private MapStructurePreprocessingProfile _defaultLiveStructureProfile;
    private string _defaultLiveStructureGenerationFingerprint = string.Empty;
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

    public Mat Image { get; }
    public MapScreenRect ClientBounds { get; }
    public MapScreenRect ViewportBounds { get; }
    public IntPtr WindowHandle { get; }

    internal MapStructureFeatures GetOrCreateDefaultLiveStructureFeatures(
        MapStructurePreprocessor preprocessor,
        out bool cacheHit,
        out double extractionMilliseconds,
        out PreprocessTiming timing,
        MapStructureGenerationTuning? generationTuning = null) =>
        GetOrCreateDefaultLiveStructureFeatures(
            preprocessor,
            MapStructurePreprocessingProfile.EdgesAndFeatures,
            out cacheHit,
            out extractionMilliseconds,
            out timing,
            generationTuning: generationTuning);

    internal MapStructureFeatures GetOrCreateDefaultLiveStructureFeatures(
        MapStructurePreprocessor preprocessor,
        MapStructurePreprocessingProfile requestedProfile,
        out bool cacheHit,
        out double extractionMilliseconds,
        out PreprocessTiming timing,
        bool generateVisibleMask = false,
        MapStructureGenerationTuning? generationTuning = null)
    {
        ArgumentNullException.ThrowIfNull(preprocessor);
        generationTuning = generationTuning?.Clone() ?? new();
        generationTuning.Normalize();
        var generationFingerprint = generationTuning.CacheFingerprint;
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_defaultLiveStructureFeatures is { } existing
                && _defaultLiveStructureTiming is { } existingTiming
                && string.Equals(
                    _defaultLiveStructureGenerationFingerprint,
                    generationFingerprint,
                    StringComparison.Ordinal))
            {
                // 可见掩码是可选派生物：若当前请求需要掩码而缓存没有
                // （旧路径生成），必须重新提取，否则 Visible-aware 会静默失效。
                // 轮廓升级路径只补描述符，不补掩码，因此需要掩码时也走重建。
                if ((!generateVisibleMask
                        || existing.RawVisibleMask is not null)
                    && _defaultLiveStructureProfile.Satisfies(requestedProfile))
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
                _defaultLiveStructureGenerationFingerprint =
                    generationFingerprint;
                _defaultLiveStructureExtractionMilliseconds =
                    upgradeStopwatch.Elapsed.TotalMilliseconds;
                existing.Dispose();
                cacheHit = false;
                extractionMilliseconds =
                    _defaultLiveStructureExtractionMilliseconds;
                timing = upgradedTiming;
                return upgraded;
            }

            if (_defaultLiveStructureFeatures is not null)
            {
                _defaultLiveStructureFeatures.Dispose();
                _defaultLiveStructureFeatures = null;
                _defaultLiveStructureTiming = null;
                _defaultLiveStructureGenerationFingerprint = string.Empty;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var created = preprocessor.ProcessLiveRoiDiagnostic(
                ComputationImage,
                null,
                null,
                out var createdTiming,
                generateVisibleMask: generateVisibleMask,
                profile: requestedProfile,
                generationTuning: generationTuning);
            stopwatch.Stop();
            _defaultLiveStructureFeatures = created;
            _defaultLiveStructureTiming = createdTiming;
            _defaultLiveStructureProfile = requestedProfile;
            _defaultLiveStructureGenerationFingerprint = generationFingerprint;
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
            _nativeObservedStructure?.Dispose();
            _nativeObservedStructure = null;
            _defaultLiveStructureFeatures?.Dispose();
            _defaultLiveStructureFeatures = null;
            _defaultLiveStructureTiming = null;
            _defaultLiveStructureGenerationFingerprint = string.Empty;
            _ownedComputationImage?.Dispose();
            _ownedComputationImage = null;
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

public sealed partial class MapScanDiagnostics
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
    public double? ScaleBootstrapHintScale { get; set; }
    public double ScaleBootstrapHintConfidence { get; set; }
    public double ScaleBootstrapSearchMinimum { get; set; }
    public double ScaleBootstrapSearchMaximum { get; set; }
    public string ScaleBootstrapMethod { get; set; } = string.Empty;
    public double ScaleBootstrapCost { get; set; }
    public double ScaleBootstrapMargin { get; set; }
    public int ScaleBootstrapTestedScaleCount { get; set; }
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
/*
 * 文件职责：MapRecognitionModels。所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 维护约束：涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定。
 */
