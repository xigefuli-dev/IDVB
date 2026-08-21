using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;

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
    private readonly object _floorPrewarmGate = new();
    private readonly Dictionary<string, Task> _floorPrewarmTasks = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Builds one floor's resident reference features at most once. Callers can
    /// overlap this work with the first capture; alignment rents the same
    /// resident entry after the task completes and never repeats the decode.
    /// </summary>
    internal Task WarmFloorStructureCacheAsync(
        MapRecord map,
        string floorKey,
        MapStructureGenerationTuning generation)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (profile is null)
            return Task.CompletedTask;
        var key = $"{map.Id:D}|{map.UpdatedAt.UtcTicks}|{floorKey}|"
            + generation.CacheFingerprint;
        lock (_floorPrewarmGate)
        {
            if (_floorPrewarmTasks.TryGetValue(key, out var existing))
                return existing;
            var task = Task.Run(() =>
            {
                if (_structureCache.TryRentResident(
                        map.Id, map.UpdatedAt, floorKey, generation) is { } resident)
                {
                    resident.Dispose();
                    return;
                }

                var path = _repository.GetFloorRecognitionPath(map, floorKey);
                using var image = Cv2.ImRead(path, ImreadModes.Unchanged);
                if (image.Empty())
                    return;
                using var prepared = _structureCache.GetOrCreate(
                    map.Id,
                    map.UpdatedAt,
                    image,
                    profile.WholeImageIgnoreRegions,
                    floorKey,
                    generation);
            });
            _floorPrewarmTasks[key] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_floorPrewarmGate)
                        _floorPrewarmTasks.Remove(key);
                },
                TaskScheduler.Default);
            return task;
        }
    }
    internal MapVpsgScaleGraphCache VpsgScaleGraphCache => _vpsgScaleGraphCache;
    internal MapVpsgScaleEstimator VpsgScaleEstimator => _vpsgScaleEstimator;
    internal MapAuxiliaryAnchorTemplateCache AuxiliaryTemplateCache => _auxiliaryTemplateCache;
    internal MapRepository Repository => _repository;

    public int ReadyMapCount => _fingerprints.Count;
    public int TotalMapCount { get; private set; }
    public int SideEntranceReadyMapCount => _sideEntranceFeatureCache.Count;

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

            var cacheDispatch = MapOperationTraceAmbient.StartChild(
                "map_catalog_fingerprint_dispatch_wait",
                MapOperationWaitKind.Queue);
            CacheBuildResult cache;
            try
            {
                cache = await Task.Run(() =>
                {
                    cacheDispatch.Complete();
                    using var cacheWorker = MapOperationTraceAmbient.StartChild(
                        "map_catalog_fingerprint_build",
                        MapOperationWaitKind.Compute);
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
            }
            finally
            {
                cacheDispatch.Complete();
            }

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
        using var recognitionRoute = MapOperationTraceAmbient.StartChild(
            "recognition_route",
            MapOperationWaitKind.Compute);
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);
        IReadOnlyList<MapGeometryFingerprint> fingerprints;
        using (var fingerprintFilter = MapOperationTraceAmbient.StartChild(
                   "fingerprint_filter",
                   MapOperationWaitKind.Compute))
        {
            fingerprints = FilterFingerprints(mapClass);
        }
        if (fingerprints.Count == 0)
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics, "没有已完成主层区域、大门和侧门标记的地图。");

        var stopwatch = Stopwatch.StartNew();
        using var preprocess = MapOperationTraceAmbient.StartChild(
            "recognition_preprocess",
            MapOperationWaitKind.Compute);
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        using var liveEdges = GateTemplateDetector.CreateEdges(frame.Image);
        preprocess.Complete();
        stopwatch.Stop();
        diagnostics.PreprocessMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        GateDetectionResult gateResult;
        using (var gateDetection = MapOperationTraceAmbient.StartChild(
                   "recognition_gate_detection",
                   MapOperationWaitKind.Compute))
        {
            gateResult = _gateDetector.Detect(
                liveMatchImage,
                frame.ViewportBounds,
                frame.ClientBounds.Width,
                tuning.GateTemplateThreshold,
                new GateSearchContext { Mode = GateSearchMode.FullSearch });
        }
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
        IReadOnlyList<MapGeometryCandidate> ranked;
        using (var geometryRanking = MapOperationTraceAmbient.StartChild(
                   "geometry_ranking",
                   MapOperationWaitKind.Compute))
        {
            ranked = MapCvRecognitionScript.RankGeometry(
                fingerprints,
                gates,
                frame.ViewportBounds,
                tuning.VectorErrorTolerance);
        }
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
                using var candidateConfirmation = MapOperationTraceAmbient.StartChild(
                    "candidate_confirmation",
                    MapOperationWaitKind.Compute,
                    mapId: candidate.Fingerprint.Map.Id.ToString("D"),
                    floorKey: candidate.Fingerprint.FloorKey,
                    attemptIndex: Array.IndexOf(confirmed, candidate));
                candidate.ConfirmationScore = MapCvRecognitionHelpers.ConfirmCandidate(
                    candidate,
                    liveEdges,
                    frame.ViewportBounds);
            }
            stopwatch.Stop();
            diagnostics.ConfirmationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            diagnostics.ConfirmationComputeMilliseconds =
                diagnostics.ConfirmationMilliseconds;
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
/*
 * 文件职责：MapCvRecognitionService。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
