using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed class RuntimeMapRecognition
{
    public MapRecord Map { get; init; } = new();
    public MapRecognitionResult Result { get; init; } = new();
    public string FloorImagePath { get; init; } = string.Empty;
}

public sealed class MapRecognitionChoice
{
    public RuntimeMapRecognition Recognition { get; init; } = new();
    public double VectorError { get; init; }
    public double RawConfidence => Recognition.Result.Confidence;
}

public sealed class MapRecognitionAttempt
{
    public RuntimeMapRecognition? Recognition { get; init; }
    public IReadOnlyList<MapRecognitionChoice> Choices { get; init; } = [];
    public MapScanDiagnostics Diagnostics { get; init; } = new();
    public string FailureReason { get; init; } = string.Empty;
    public MapStructureRegistrationResult? StructureResult { get; init; }

    public GateDetectionResult? GateDetectionResult { get; init; }
    public bool StructureAttempted { get; init; }
    public bool StructureAccepted { get; init; }
    public string StructureFailureReason { get; init; } = string.Empty;
    public AlignmentSearchStage SearchStage { get; init; }
}

internal enum SelectedAlignmentRoute
{
    Default,
    SideEntrance
}

/// <summary>Application-lifetime primary-floor gate detector and geometry recognizer.</summary>
public sealed partial class MapCvRecognitionService : IDisposable
{
    // 侧门策略下，若本次已由单门/辅助锚点得到新鲜的视口位置，结构配准结果
    // 与之偏差超过该阈值即视为冲突并拒绝，避免接受锚点位置之外的漂移结果。
    // 该检查只在存在新鲜锚点位置时启用，不限制无门冷启动的整图平移恢复。
    internal const double SideEntranceAnchorDeviationTolerancePixels = 60d;

    private sealed record CacheBuildResult(
        IReadOnlyList<MapRecord> Maps,
        IReadOnlyList<MapGeometryFingerprint> Fingerprints);

    private readonly MapRepository _repository;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly GateTemplateDetector _gateDetector;
    private readonly MapStructurePreprocessor _structurePreprocessor = new();
    private readonly MapStructureRegistrar _structureRegistrar;
    private readonly MapStructureReferenceCache _structureCache;
    private readonly MapVpsgScaleGraphCache _vpsgScaleGraphCache = new();
    private readonly MapVpsgScaleEstimator _vpsgScaleEstimator = new();
    private readonly MapAuxiliaryAnchorTemplateCache _auxiliaryTemplateCache =
        new();
    private readonly SideEntranceScanPipeline _sideEntrancePipeline = new();
    // 侧门特征缓存：(mapId, floorKey) → 预加载的灰度模板 Mat
    private Dictionary<(Guid, string), Mat> _sideEntranceFeatureCache = [];
    private MapCatalogRevision _catalogRevision = MapCatalogRevision.Empty;
    private IReadOnlyList<MapRecord> _maps = [];
    private IReadOnlyList<MapGeometryFingerprint> _fingerprints = [];
    private bool _cacheInitialized;
    private bool _disposed;

    public MapCvRecognitionService(MapRepository repository)
    {
        _repository = repository;
        _gateDetector = new GateTemplateDetector(MapCvRecognitionHelpers.ResolveGatePath());
        _structureRegistrar = new MapStructureRegistrar(_structurePreprocessor);
        _structureCache = new MapStructureReferenceCache(_structurePreprocessor);
    }

    // ── Internal accessors for static helper classes ────────────────────────────

    internal bool IsDisposed => _disposed;
    internal GateTemplateDetector GateDetector => _gateDetector;
    internal MapStructurePreprocessor StructurePreprocessor => _structurePreprocessor;
    internal MapStructureRegistrar StructureRegistrar => _structureRegistrar;
    internal MapStructureReferenceCache StructureCache => _structureCache;
    internal MapVpsgScaleGraphCache VpsgScaleGraphCache => _vpsgScaleGraphCache;
    internal MapVpsgScaleEstimator VpsgScaleEstimator => _vpsgScaleEstimator;
    internal MapAuxiliaryAnchorTemplateCache AuxiliaryTemplateCache => _auxiliaryTemplateCache;
    internal MapRepository Repository => _repository;

    public int ReadyMapCount => _fingerprints.Count;
    public int TotalMapCount { get; private set; }

    /// <summary>上次成功检测到的门模板 scale（用于 LockedScale 搜索），可能为 null。</summary>
    public double? LastGateTemplateScale => _gateDetector.WarmScale;

    /// <summary>
    /// Clears observations learned from one match without discarding the map
    /// catalog or immutable derived-reference caches.
    /// </summary>
    public void ResetMatchState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gateDetector.ResetSuccessfulScale();
    }

    public MapRecord? TryGetMap(Guid mapId) =>
        _maps.FirstOrDefault(map => map.Id == mapId)?.Clone();

    public async Task RefreshCacheAsync(Guid? changedMapId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var revision = _repository.GetCatalogRevision();
        if (_cacheInitialized && revision == _catalogRevision)
            return;

        await _cacheGate.WaitAsync();
        try
        {
            revision = _repository.GetCatalogRevision();
            if (_cacheInitialized && revision == _catalogRevision)
                return;
            var maps = await _repository.GetMapsAsync();
            await _repository.EnsureDerivedAssetsAsync(maps);

            var cache = await Task.Run(() =>
            {
                var snapshot = maps.Select(map => map.Clone()).ToArray();
                if (!_cacheInitialized)
                {
                    return new CacheBuildResult(
                        snapshot,
                        snapshot.Select(TryCreateFingerprint)
                            .Where(fingerprint => fingerprint is not null)
                            .Cast<MapGeometryFingerprint>()
                            .ToArray());
                }

                var previousMaps = _maps.ToDictionary(map => map.Id);
                var previousFingerprints = _fingerprints.ToDictionary(
                    fingerprint => fingerprint.Map.Id);
                var changedIds = snapshot
                    .Where(map => changedMapId == map.Id
                        || !previousMaps.TryGetValue(map.Id, out var previous)
                        || !MapCvRecognitionHelpers.HaveSameFingerprintInputs(previous, map))
                    .Select(map => map.Id)
                    .ToHashSet();

                var fingerprints = new List<MapGeometryFingerprint>();
                foreach (var map in snapshot)
                {
                    if (!changedIds.Contains(map.Id)
                        && previousFingerprints.TryGetValue(map.Id, out var existing))
                    {
                        fingerprints.Add(RebindFingerprint(existing, map));
                        continue;
                    }

                    if (TryCreateFingerprint(map) is { } rebuilt)
                        fingerprints.Add(rebuilt);
                }

                return new CacheBuildResult(snapshot, fingerprints);
            });

            TotalMapCount = cache.Maps.Count;
            _maps = cache.Maps;
            _fingerprints = cache.Fingerprints;
            _catalogRevision = _repository.GetCatalogRevision();
            _cacheInitialized = true;
            // MapRepository may overwrite an image without changing its
            // path. The overlay bitmap cache must not retain that old image.
            MapOverlayBitmapRenderer.InvalidateImageCache();

            // 刷新侧门特征缓存
            var oldFeatureCache = _sideEntranceFeatureCache;
            _sideEntranceFeatureCache = MapCvRecognitionHelpers.BuildSideEntranceFeatureCache(_repository, cache.Maps);
            foreach (var mat in oldFeatureCache.Values)
                mat.Dispose();
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static MapGeometryFingerprint RebindFingerprint(
        MapGeometryFingerprint source,
        MapRecord map) => new()
    {
        Map = map,
        FloorKey = source.FloorKey,
        MainPoint = source.MainPoint,
        SidePoint = source.SidePoint,
        MainReferenceBounds = source.MainReferenceBounds,
        SideReferenceBounds = source.SideReferenceBounds,
        ReferenceWidth = source.ReferenceWidth,
        ReferenceHeight = source.ReferenceHeight,
        RecognitionImagePath = source.RecognitionImagePath,
        OverlayImagePath = source.OverlayImagePath,
        ReferenceGateIconWidth = source.ReferenceGateIconWidth,
        ReferenceGateIconHeight = source.ReferenceGateIconHeight
    };

    // ── 公开识别入口 ──────────────────────────────────────────────────────────

    public MapRecognitionAttempt Recognize(
        CapturedGameFrame frame,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        tuning.ForceBestRecognitionResult = false;
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);
        var fingerprints = FilterFingerprints(mapClass);
        if (fingerprints.Count == 0)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "没有已完成主层区域、大门和侧门标记的地图。");

        var stopwatch = Stopwatch.StartNew();
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        using var liveEdges = GateTemplateDetector.CreateEdges(frame.Image);
        stopwatch.Stop();
        diagnostics.PreprocessMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        var gateResult = _gateDetector.Detect(
            liveMatchImage,
            frame.ViewportBounds,
            frame.ClientBounds.Width,
            tuning.GateTemplateThreshold,
            new GateSearchContext { Mode = GateSearchMode.FullSearch });
        var gates = gateResult.Gates;
        stopwatch.Stop();
        diagnostics.GateDetectionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        diagnostics.GateCandidateCount = gates.Count;
        diagnostics.GateSearchMode = gateResult.SearchModeUsed;
        diagnostics.GateSearchStopReason = gateResult.StopReason;
        diagnostics.GateScalesEvaluated = gateResult.ScalesEvaluated;
        diagnostics.GateMatchTemplateCalls = gateResult.MatchTemplateCalls;
        diagnostics.GateBudgetExceeded = gateResult.BudgetExceeded;
        MapLogCollector.Instance.Append(MapLogCategory.GateDetection, MapLogLevel.Info,
            $"检测到 {gates.Count} 个门候选 · 模式 {gateResult.SearchModeUsed}",
            elapsedMs: diagnostics.GateDetectionMilliseconds,
            details: new()
            {
                ["gateCount"] = gates.Count,
                ["threshold"] = tuning.GateTemplateThreshold,
                ["mode"] = gateResult.SearchModeUsed.ToString(),
                ["stopReason"] = gateResult.StopReason.ToString(),
                ["scalesEvaluated"] = gateResult.ScalesEvaluated,
            });
        if (gates.Count < 2)
        {
            MapLogCollector.Instance.Append(MapLogCategory.GateDetection, MapLogLevel.Warning,
                $"门候选不足：只找到 {gates.Count} 个（阈值 {tuning.GateTemplateThreshold:P0}）");
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"只找到 {gates.Count} 个可靠门图标（阈值 {tuning.GateTemplateThreshold:P0}）。可使用手动识别绑定框选大门和侧门。");
        }

        stopwatch.Restart();
        var ranked = MapCvRecognitionScript.RankGeometry(
            fingerprints,
            gates,
            frame.ViewportBounds,
            tuning.VectorErrorTolerance);
        stopwatch.Stop();
        diagnostics.GeometryMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        MapLogCollector.Instance.Append(MapLogCategory.GeometryRanking, MapLogLevel.Info,
            $"几何排名完成 · 首位地图 {ranked[0].Fingerprint.Map.SequenceNumber}",
            elapsedMs: diagnostics.GeometryMilliseconds,
            details: new()
            {
                ["topScore"] = ranked[0].VectorError,
                ["candidateCount"] = ranked.Count,
                ["topMapId"] = ranked[0].Fingerprint.Map.Id
            });
        var margin = MapCvRecognitionHelpers.GeometryMargin(ranked);
        if (!MapCvRecognitionDiagnostics.TryValidateRanking(
                ranked, tuning, diagnostics, out var failure))
        {
            return MapCvRecognitionBuilders.FailureWithChoices(
                ranked,
                alignmentMode,
                tuning,
                margin,
                MapRecognitionSource.Automatic,
                diagnostics,
                failure!.FailureReason);
        }

        var winner = ranked[0];
        var usedConfirmation = false;
        var forcedBestResult = false;
        if (ranked.Count > 1 && margin < tuning.AmbiguityMargin)
        {
            usedConfirmation = true;
            stopwatch.Restart();
            var confirmed = ranked
                .Take(4)
                .Where(candidate => candidate.VectorError <= tuning.VectorErrorTolerance)
                .ToArray();
            foreach (var candidate in confirmed)
            {
                candidate.ConfirmationScore = MapCvRecognitionHelpers.ConfirmCandidate(
                    candidate,
                    liveEdges,
                    frame.ViewportBounds);
            }
            stopwatch.Stop();
            diagnostics.ConfirmationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            var confirmationRanking = confirmed
                .OrderByDescending(candidate => candidate.ConfirmationScore)
                .ThenBy(candidate => candidate.VectorError)
                .ToArray();
            if (confirmationRanking.Length < 2
                || confirmationRanking[0].ConfirmationScore
                    - confirmationRanking[1].ConfirmationScore
                    < tuning.ConfirmationAdvantage)
            {
                if (!tuning.ForceBestRecognitionResult)
                {
                    return MapCvRecognitionBuilders.FailureWithChoices(
                        ranked,
                        alignmentMode,
                        tuning,
                        margin,
                        MapRecognitionSource.Automatic,
                        diagnostics,
                        $"地图 {ranked[0].Fingerprint.Map.SequenceNumber} 等候选仍然过于接近。");
                }
                winner = confirmationRanking.FirstOrDefault() ?? winner;
                forcedBestResult = true;
            }
            else
            {
                winner = confirmationRanking[0];
            }
        }

        if (winner.VectorError > tuning.VectorErrorTolerance)
            return MapCvRecognitionBuilders.FailureWithChoices(
                ranked,
                alignmentMode,
                tuning,
                margin,
                MapRecognitionSource.Automatic,
                diagnostics,
                $"地图区域或双门坐标不一致，请重新校准（误差 {winner.VectorError:F3}，阈值 {tuning.VectorErrorTolerance:F3}）。");
        if (!MapCvRecognitionBuilders.TryBuildRecognition(
                winner,
                alignmentMode,
                tuning,
                margin,
                usedConfirmation,
                MapRecognitionSource.Automatic,
                forcedBestResult,
                out var recognition,
                out var transformFailure))
        {
            return MapCvRecognitionBuilders.FailureWithChoices(
                ranked,
                alignmentMode,
                tuning,
                margin,
                MapRecognitionSource.Automatic,
                diagnostics,
                $"无法安全对齐地图图层：{transformFailure}");
        }
        if (recognition!.Result.Confidence < tuning.MinimumConfidence
            && !tuning.ForceBestRecognitionResult)
        {
            return MapCvRecognitionBuilders.FailureWithChoices(
                ranked,
                alignmentMode,
                tuning,
                margin,
                MapRecognitionSource.Automatic,
                diagnostics,
                $"识别置信度 {recognition.Result.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}，未显示地图。");
        }

        _gateDetector.RememberSuccessfulScale(
            (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
        diagnostics.UsedForcedBestResult =
            recognition.Result.WasForcedBestResult;
        diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
        if (tuning.ForceCandidateSelection)
        {
            var forcedChoices = MapCvRecognitionBuilders.BuildChoices(
                ranked, alignmentMode, tuning, margin,
                MapRecognitionSource.Automatic);
            if (forcedChoices.Count > 0)
            {
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Choices = forcedChoices,
                    FailureReason = "强制候选模式已开启，请选择正确地图。"
                };
            }
        }
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Recognition = recognition
        };
    }

    public MapRecognitionAttempt AlignSelected(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession? session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        AlignmentSearchContext? alignmentSearchContext = null,
        double nativeScaleChangeRatio = MapSessionRules.NativeScaleChangeRatio,
        string? mapClass = null) =>
        MapCvAlignmentService.AlignSelectedCore(
            this,
            frame,
            selectedMapId,
            session,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            alignmentSearchContext,
            nativeScaleChangeRatio,
            mapClass,
            SelectedAlignmentRoute.Default);

    /// <summary>
    /// Side-entrance tracking uses one identified gate when possible and falls
    /// back to static structure. It never ranks or commits a dual-gate pair.
    /// </summary>
    public MapRecognitionAttempt AlignSideEntrance(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        AlignmentSearchContext? alignmentSearchContext = null,
        double nativeScaleChangeRatio = MapSessionRules.NativeScaleChangeRatio,
        string? mapClass = null)
    {
        // The selected-map confirmation path already supplies a warm-search
        // context.  Re-open alignment used to omit it, which silently changed
        // every later side-entrance alignment into a FullSearch.  Reconstruct
        // the same narrow gate-scale prior here so all callers keep the side
        // route semantics.
        alignmentSearchContext ??= CreateSideEntranceWarmSearchContext(
            session,
            tuning);

        return MapCvAlignmentService.AlignSelectedCore(
            this,
            frame,
            selectedMapId,
            session,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            alignmentSearchContext,
            nativeScaleChangeRatio,
            mapClass,
            SelectedAlignmentRoute.SideEntrance);
    }

    private static AlignmentSearchContext? CreateSideEntranceWarmSearchContext(
        MapAlignmentSession session,
        MapRecognitionTuning tuning)
    {
        var warmScale = session.GateTemplateScale
            ?? (GateTemplateRules.ReferenceScale * session.BaselineGateScale);
        if (!double.IsFinite(warmScale) || warmScale <= 0d)
            return null;

        var context = new AlignmentSearchContext
        {
            GateSearch = new GateSearchContext
            {
                Mode = GateSearchMode.WarmScaleSearch,
                WarmScale = warmScale,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold = GateTemplateRules.EarlyExitScoreThreshold,
                SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
                AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
            }
        };
        if (tuning.WarmGateSearchBudgetMs > 0)
            context.GateSearch.TimeBudgetMilliseconds =
                tuning.WarmGateSearchBudgetMs;
        return context;
    }

    /// <summary>
    /// Aligns one exact non-primary floor from its own static structure. This
    /// path never calls gate or auxiliary-anchor detection and never inherits
    /// translation from another floor.
    /// </summary>
    public MapRecognitionAttempt AlignFloorWithoutGates(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        bool isTracking = false,
        bool useProjectedBoundaryMask = false,
        MapScaleSearchPolicy scaleSearchPolicy = MapScaleSearchPolicy.Search,
        double identityPriorConfidence = 0d) =>
        MapCvAlignmentService.AlignStructureOnly(
            this,
            frame,
            selectedMapId,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            isTracking,
            useProjectedBoundaryMask,
            allowPrimaryFloor: false,
            scaleSearchPolicy,
            identityPriorConfidence);

    public MapRecognitionAttempt AlignWithCachedScale(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        double identityPriorConfidence = 0d) =>
        MapCvAlignmentService.AlignStructureOnly(
            this,
            frame,
            selectedMapId,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior: null,
            predictedViewportOrigin: null,
            liveIgnoreRegions: null,
            candidateHistory: null,
            isTracking: false,
            useProjectedBoundaryMask: false,
            allowPrimaryFloor: true,
            scaleSearchPolicy: MapScaleSearchPolicy.Fixed,
            identityPriorConfidence);

    /// <summary>
    /// Thin wrapper that reuses AlignSelected for confirmation frames.
    /// Uses local ROI search around predicted gate positions and
    /// restricted structure fallback — never upgrades to FullSearch.
    /// </summary>
    public MapRecognitionAttempt ConfirmSelectedAlignment(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession session,
        MapRecognitionAttempt previousAttempt,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning = null,
        MapReferencePoint? playerPrior = null,
        MapViewportOrigin? predictedViewportOrigin = null,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions = null,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory = null,
        double nativeScaleChangeRatio = MapSessionRules.NativeScaleChangeRatio,
        string? mapClass = null) =>
        MapCvAlignmentService.ConfirmSelectedAlignment(
            this,
            frame,
            selectedMapId,
            session,
            previousAttempt,
            alignmentMode,
            tuning,
            structureTuning,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            nativeScaleChangeRatio,
            mapClass);

    public MapRecognitionAttempt RecognizeManual(
        MapScreenRect viewportBounds,
        MapScreenRect mainGateBounds,
        MapScreenRect sideGateBounds,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);
        diagnostics.GateCandidateCount = 2;
        var fingerprints = FilterFingerprints(mapClass);
        if (fingerprints.Count == 0)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "没有已完成主层区域、大门和侧门标记的地图。");
        if (!viewportBounds.IsValid || !mainGateBounds.IsValid || !sideGateBounds.IsValid)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "手动框选的地图区域或门矩形无效。");

        var gates = new[]
        {
            new GateDetection
            {
                Score = 1d,
                Scale = 0d,
                ScreenBounds = mainGateBounds
            },
            new GateDetection
            {
                Score = 1d,
                Scale = 0d,
                ScreenBounds = sideGateBounds
            }
        };
        var stopwatch = Stopwatch.StartNew();
        var ranked = MapCvRecognitionScript.RankGeometry(
            fingerprints,
            gates,
            viewportBounds,
            tuning.VectorErrorTolerance,
            testSwappedAssignments: false);
        stopwatch.Stop();
        diagnostics.GeometryMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        if (!MapCvRecognitionDiagnostics.TryValidateRanking(
                ranked, tuning, diagnostics, out var failure))
        {
            // 即使不满足排名门槛，玩家手动框了门就值得展示候选供选择
            var rescueChoices = MapCvRecognitionBuilders.BuildChoices(
                ranked, alignmentMode, tuning, double.PositiveInfinity,
                MapRecognitionSource.ManualGateSelection);
            if (rescueChoices.Count > 0)
            {
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Choices = rescueChoices,
                    FailureReason = failure!.FailureReason + " 请从候选中选择。"
                };
            }
            return failure!;
        }

        var margin = MapCvRecognitionHelpers.GeometryMargin(ranked);
        var choices = MapCvRecognitionBuilders.BuildChoices(
            ranked, alignmentMode, tuning, margin,
            MapRecognitionSource.ManualGateSelection);
        if (choices.Count == 0)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "手动双门坐标无法生成安全的无旋转缩放与位移。");

        diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
        var winner = choices[0].Recognition;
        if (!tuning.ForceCandidateSelection
            && margin >= tuning.AmbiguityMargin
            && winner.Result.Confidence >= tuning.MinimumConfidence)
        {
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = winner
            };
        }
        if (!tuning.ForceCandidateSelection
            && tuning.ForceBestRecognitionResult)
        {
            diagnostics.UsedForcedBestResult = true;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = MapCvRecognitionBuilders.MarkForcedBestResult(winner)
            };
        }

        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Choices = choices,
            FailureReason = tuning.ForceCandidateSelection
                ? "强制候选模式已开启，请选择正确地图。"
                : winner.Result.Confidence < tuning.MinimumConfidence
                    ? $"最高置信度 {winner.Result.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}，请选择正确地图。"
                    : "前几名地图过于接近，请选择正确地图。"
        };
    }

    internal IReadOnlyList<MapGeometryFingerprint> FilterFingerprints(string? mapClass)
    {
        if (string.IsNullOrWhiteSpace(mapClass))
            return _fingerprints;

        return _fingerprints
            .Where(fingerprint => string.Equals(
                fingerprint.Map.Class,
                mapClass,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static RuntimeMapRecognition ConfirmChoice(MapRecognitionChoice choice)
    {
        var original = choice.Recognition;
        var result = original.Result;
        return new RuntimeMapRecognition
        {
            Map = original.Map,
            FloorImagePath = original.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = 0,
                Confidence = result.Confidence,
                Source = MapRecognitionSource.UserConfirmed,
                HasAllRequiredAnchorEvidence = result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = result.OverlayTransform,
                AnchorMatches = result.AnchorMatches,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation =
                    result.SkippedStructureValidation,
                WasForcedBestResult = result.WasForcedBestResult,
                ReusedLastTransform = result.ReusedLastTransform,
                UsedCachedScale = result.UsedCachedScale
            }
        };
    }

    private MapGeometryFingerprint? TryCreateFingerprint(MapRecord map)
    {
        map.NormalizeRecognition();
        if (!map.Recognition.HasRequiredIdentificationData())
            return null;
        var floorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? map.Recognition.FirstFloor;
        var main = profile.FindAnchor("main-entrance");
        var side = profile.FindAnchor("side-entrance");
        if (main?.Bounds?.IsValid is not true
            || side?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }
        var mainRefBounds = MapCvRecognitionHelpers.ToPixelBounds(
            main.Bounds,
            profile.RecognitionPixelWidth,
            profile.RecognitionPixelHeight);
        var sideRefBounds = MapCvRecognitionHelpers.ToPixelBounds(
            side.Bounds,
            profile.RecognitionPixelWidth,
            profile.RecognitionPixelHeight);
        var recognitionImagePath = _repository.GetFloorRecognitionPath(map, floorKey);

        // Measure actual gate icon size in the reference image so that
        // EstimateAxisScale uses comparable objects on both sides of the
        // ratio (screen-side: template-matched tight box; reference-side:
        // template-matched tight box) instead of comparing a tight box to
        // a user-drawn loose anchor rectangle.
        double iconWidth = 0d;
        double iconHeight = 0d;
        try
        {
            using var reference = Cv2.ImRead(recognitionImagePath, ImreadModes.Unchanged);
            if (!reference.Empty())
            {
                var mainCenter = new Point2d(
                    mainRefBounds.CenterX,
                    mainRefBounds.CenterY);
                var sideCenter = new Point2d(
                    sideRefBounds.CenterX,
                    sideRefBounds.CenterY);
                var mainSize = GateTemplateDetector.EstimateReferenceGateIconSize(
                    reference,
                    mainCenter);
                var sideSize = GateTemplateDetector.EstimateReferenceGateIconSize(
                    reference,
                    sideCenter);
                if (mainSize is { } mainSz && sideSize is { } sideSz)
                {
                    // Average the two measurements — they should be very close.
                    iconWidth = (mainSz.Width + sideSz.Width) / 2d;
                    iconHeight = (mainSz.Height + sideSz.Height) / 2d;
                }
                else if (mainSize is { } mSz)
                {
                    iconWidth = mSz.Width;
                    iconHeight = mSz.Height;
                }
                else if (sideSize is { } sSz)
                {
                    iconWidth = sSz.Width;
                    iconHeight = sSz.Height;
                }
            }
        }
        catch
        {
            // Reference image missing or corrupt — fall back to anchor bounds.
        }

        return new MapGeometryFingerprint
        {
            Map = map,
            FloorKey = floorKey,
            MainPoint = MapCvRecognitionHelpers.Center(main.Bounds),
            SidePoint = MapCvRecognitionHelpers.Center(side.Bounds),
            MainReferenceBounds = mainRefBounds,
            SideReferenceBounds = sideRefBounds,
            ReferenceWidth = profile.RecognitionPixelWidth,
            ReferenceHeight = profile.RecognitionPixelHeight,
            RecognitionImagePath = recognitionImagePath,
            OverlayImagePath = _repository.GetFloorOverlayPath(map, floorKey),
            ReferenceGateIconWidth = iconWidth,
            ReferenceGateIconHeight = iconHeight
        };
    }

    // ── 侧门特征缓存与扫描 ────────────────────────────────────────────

    /// <summary>
    /// 使用侧门特征缓存对捕获帧执行模板匹配，返回 top-<paramref name="topK"/> 候选。
    /// </summary>
    public IReadOnlyList<SideEntranceScanCandidate> RunSideEntranceScan(
        Mat capturedFrame,
        int topK = 5,
        string? mapClass = null,
        Guid? selectedMapId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (capturedFrame.Empty() || _sideEntranceFeatureCache.Count == 0)
            return [];

        var candidates = new List<(MapRecord map, string floorKey, Mat template)>(
            _sideEntranceFeatureCache.Count);

        foreach (var ((mapId, floorKey), template) in _sideEntranceFeatureCache)
        {
            var map = _maps.FirstOrDefault(m => m.Id == mapId);
            if (map is null
                || (selectedMapId is { } requiredMapId
                    && map.Id != requiredMapId)
                || (!string.IsNullOrWhiteSpace(mapClass)
                    && !string.Equals(
                        map.Class,
                        mapClass,
                        StringComparison.OrdinalIgnoreCase))
                || !string.Equals(
                    floorKey,
                    MapFloorRules.GetPrimaryFloorKey(map),
                    StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add((map, floorKey, template));
        }

        return _sideEntrancePipeline.RunScan(capturedFrame, candidates, topK);
    }

    /// <summary>
    /// Runs the side-entrance identity scan with the mandatory gate evidence.
    /// The user-authored side-entrance feature remains the map discriminator,
    /// but it can no longer identify a map when the live frame contains no
    /// detectable gate.
    /// </summary>
    public SideEntranceScanResult RunSideEntranceScan(
        CapturedGameFrame frame,
        MapRecognitionTuning tuning,
        int topK = 5,
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        var gateResult = _gateDetector.Detect(
            liveMatchImage,
            frame.ViewportBounds,
            frame.ClientBounds.Width,
            tuning.GateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
                AllowSingleGateEarlyExit = true,
                SingleGateScoreThreshold =
                    Math.Max(tuning.GateTemplateThreshold, GateTemplateRules.EarlyExitScoreThreshold),
                SingleGateScaleTolerance = GateTemplateRules.SingleGateScaleTolerance,
                AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap
            });

        var gate = gateResult.Gates
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (gate is null)
        {
            return new SideEntranceScanResult
            {
                GateDetection = gateResult,
                FailureReason =
                    "side-entrance scan requires one visible gate feature; no gate was detected."
            };
        }

        var candidates = RunSideEntranceScan(
            frame.Image,
            topK,
            mapClass);
        return new SideEntranceScanResult
        {
            GateDetection = gateResult,
            Candidates = candidates,
            FailureReason = candidates.Count == 0
                ? "the visible gate was found, but no marked side-entrance feature matched a map."
                : string.Empty
        };
    }

    /// <summary>
    /// Builds the provisional selected-map result used by the candidate UI.
    /// It is scan evidence only; the caller must run AlignSideEntrance after
    /// the user confirms the map.
    /// </summary>
    public bool TryCreateSideEntranceSelection(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewportBounds,
        out RuntimeMapRecognition recognition,
        out MapAlignmentSession session,
        out string failureReason)
    {
        recognition = new RuntimeMapRecognition();
        if (!TryCreateSideEntranceAlignmentSeed(
                candidate,
                gate,
                viewportBounds,
                out session,
                out failureReason))
        {
            return false;
        }

        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == candidate.Map.Id
            && string.Equals(item.FloorKey, candidate.FloorKey, StringComparison.Ordinal));
        if (fingerprint is null)
        {
            failureReason = "the selected side-entrance candidate is no longer in the map cache.";
            return false;
        }

        var confidence = Math.Clamp(
            (candidate.MatchScore * 0.70d) + (gate.Score * 0.30d),
            0d,
            1d);
        recognition = MapCvRecognitionBuilders.BuildTrackedRecognition(
            fingerprint,
            session.LockedTransform,
            session.LockedGateEvidence,
            MapRecognitionSource.SideEntranceSelection,
            confidenceOverride: confidence,
            evidenceKind: MapAlignmentEvidenceKind.None);
        return true;
    }

    public bool TryCreateSideEntranceAlignmentSeed(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewportBounds,
        out MapAlignmentSession session,
        out string failureReason)
    {
        var fingerprint = _fingerprints.FirstOrDefault(item =>
            item.Map.Id == candidate.Map.Id
            && string.Equals(item.FloorKey, candidate.FloorKey, StringComparison.Ordinal));
        if (fingerprint is null)
        {
            session = new MapAlignmentSession();
            failureReason = "the selected side-entrance candidate is no longer in the map cache.";
            return false;
        }

        return SideEntranceScanPipeline.TryCreateGateAlignmentSeed(
            candidate,
            gate,
            viewportBounds,
            fingerprint.ReferenceGateIconWidth,
            fingerprint.ReferenceGateIconHeight,
            out session,
            out failureReason);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gateDetector.Dispose();
        _structureCache.Dispose();
        _auxiliaryTemplateCache.Dispose();
        _cacheGate.Dispose();
        foreach (var mat in _sideEntranceFeatureCache.Values)
            mat.Dispose();
        _sideEntranceFeatureCache = [];
    }
}
