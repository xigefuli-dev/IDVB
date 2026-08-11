using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

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

}
