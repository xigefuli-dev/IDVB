using OpenCvSharp;
using System.Diagnostics;
using System.Text.Json;

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

public sealed class AlignmentSearchContext
{
    public required GateSearchContext GateSearch { get; init; }

    public bool UseRestrictedStructureFallback { get; init; }
    public bool RequireCurrentFrameEvidence { get; init; }
    public bool AllowFullSearchUpgrade { get; init; }

    public MapRecognitionAttempt? PreviousAttempt { get; init; }
    public MapSimilarityTransform? ExpectedTransform { get; init; }
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

/// <summary>Application-lifetime primary-floor gate detector and geometry recognizer.</summary>
public sealed class MapCvRecognitionService : IDisposable
{
    private sealed record CacheBuildResult(
        IReadOnlyList<MapRecord> Maps,
        IReadOnlyList<MapGeometryFingerprint> Fingerprints);

    private readonly MapRepository _repository;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly GateTemplateDetector _gateDetector;
    private readonly MapStructurePreprocessor _structurePreprocessor = new();
    private readonly MapStructureRegistrar _structureRegistrar;
    private readonly MapStructureReferenceCache _structureCache;
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
        _gateDetector = new GateTemplateDetector(ResolveGatePath());
        _structureRegistrar = new MapStructureRegistrar(_structurePreprocessor);
        _structureCache = new MapStructureReferenceCache(_structurePreprocessor);
    }

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
                        || !HaveSameFingerprintInputs(previous, map))
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
            _sideEntranceFeatureCache = BuildSideEntranceFeatureCache(cache.Maps);
            foreach (var mat in oldFeatureCache.Values)
                mat.Dispose();
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static bool HaveSameFingerprintInputs(MapRecord left, MapRecord right)
    {
        if (left.Id != right.Id
            || left.UpdatedAt != right.UpdatedAt
            || left.Recognition.SchemaVersion != right.Recognition.SchemaVersion)
            return false;

        var leftFloors = MapFloorRules.GetOrderedFloors(left);
        var rightFloors = MapFloorRules.GetOrderedFloors(right);
        if (leftFloors.Count != rightFloors.Count)
            return false;

        for (var index = 0; index < leftFloors.Count; index++)
        {
            var a = leftFloors[index];
            var b = rightFloors[index];
            if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)
                || !string.Equals(a.ImageSha256, b.ImageSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.RecognitionSha256, b.RecognitionSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.OverlaySha256, b.OverlaySha256, StringComparison.OrdinalIgnoreCase)
                || a.ImageFileLength != b.ImageFileLength
                || a.ImageLastWriteUtcTicks != b.ImageLastWriteUtcTicks
                || a.RecognitionFileLength != b.RecognitionFileLength
                || a.RecognitionLastWriteUtcTicks != b.RecognitionLastWriteUtcTicks
                || a.OverlayFileLength != b.OverlayFileLength
                || a.OverlayLastWriteUtcTicks != b.OverlayLastWriteUtcTicks)
                return false;
        }

        return true;
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

    public MapRecognitionAttempt Recognize(
        CapturedGameFrame frame,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = NormalizedCopy(tuning);
        tuning.ForceBestRecognitionResult = false;
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        var diagnostics = CreateDiagnostics();
        var fingerprints = FilterFingerprints(mapClass);
        if (fingerprints.Count == 0)
            return Failure(diagnostics, "没有已完成主层区域、大门和侧门标记的地图。");

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
            return Failure(
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
            details: new() { ["topScore"] = ranked[0].VectorError, ["candidateCount"] = ranked.Count, ["topMapId"] = ranked[0].Fingerprint.Map.Id });
        var margin = GeometryMargin(ranked);
        if (!TryValidateRanking(ranked, tuning, diagnostics, out var failure))
            return FailureWithChoices(diagnostics, ranked, alignmentMode, tuning,
                margin, MapRecognitionSource.Automatic, failure!.FailureReason);

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
                candidate.ConfirmationScore = ConfirmCandidate(
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
                    return FailureWithChoices(
                        diagnostics,
                        ranked,
                        alignmentMode,
                        tuning,
                        margin,
                        MapRecognitionSource.Automatic,
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
            return FailureWithChoices(diagnostics, ranked, alignmentMode, tuning,
                margin, MapRecognitionSource.Automatic,
                $"地图区域或双门坐标不一致，请重新校准（误差 {winner.VectorError:F3}，阈值 {tuning.VectorErrorTolerance:F3}）。");
        if (!TryBuildRecognition(
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
            return FailureWithChoices(diagnostics, ranked, alignmentMode, tuning,
                margin, MapRecognitionSource.Automatic,
                $"无法安全对齐地图图层：{transformFailure}");
        }
        if (recognition!.Result.Confidence < tuning.MinimumConfidence
            && !tuning.ForceBestRecognitionResult)
        {
            return FailureWithChoices(
                diagnostics,
                ranked,
                alignmentMode,
                tuning,
                margin,
                MapRecognitionSource.Automatic,
                $"识别置信度 {recognition.Result.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}，未显示地图。");
        }

        _gateDetector.RememberSuccessfulScale(
            (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
        diagnostics.UsedForcedBestResult =
            recognition.Result.WasForcedBestResult;
        diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
        if (tuning.ForceCandidateSelection)
        {
            var forcedChoices = BuildChoices(
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
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = NormalizedCopy(tuning);
        tuning.ForceBestRecognitionResult = false;
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        structureTuning ??= new MapStructureRegistrationTuning();
        structureTuning = structureTuning.Clone();
        structureTuning.Normalize();
        var diagnostics = CreateDiagnostics();
        var searchCtx = alignmentSearchContext;

        diagnostics.SearchStage =
            searchCtx?.GateSearch.Mode switch
            {
                GateSearchMode.FullSearch => AlignmentSearchStage.FullGateSearch,
                GateSearchMode.WarmScaleSearch => AlignmentSearchStage.WarmGateSearch,
                GateSearchMode.LockedScale => AlignmentSearchStage.LockedGateSearch,
                GateSearchMode.LocalConfirmationSearch =>
                    AlignmentSearchStage.LocalGateConfirmation,
                _ => AlignmentSearchStage.None,
            };
        var fingerprint = FilterFingerprints(mapClass).FirstOrDefault(
            candidate => candidate.Map.Id == selectedMapId);
        if (fingerprint is null)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return Failure(
                diagnostics,
                "当前选择的地图不存在或尚未完成主层区域与双门标记；地图序号没有被删除。");
        }
        var compatibleSession = session is not null
            && session.MapId == selectedMapId
            && session.MapUpdatedAt == fingerprint.Map.UpdatedAt
            && session.LockedTransform.AlignmentMode == alignmentMode
                ? session
                : null;

        var stopwatch = Stopwatch.StartNew();
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        stopwatch.Stop();
        diagnostics.PreprocessMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        var gateContext = searchCtx?.GateSearch
            ?? new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
            };
        var gateResult = _gateDetector.Detect(
            liveMatchImage,
            frame.ViewportBounds,
            frame.ClientBounds.Width,
            tuning.GateTemplateThreshold,
            gateContext);
        var gates = gateResult.Gates;
        stopwatch.Stop();
        diagnostics.GateDetectionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        // ── LockedScale safety net ───────────────────────────────────────────────
        // When the locked scale produces too few or too weak detections,
        // fall back to WarmScaleSearch before proceeding to the single-gate
        // or structure path. This is placed in AlignSelected (not in
        // GateTemplateDetector) because only the caller can judge whether
        // the detection result is "good enough" for the downstream pipeline.
        if (gateContext.Mode == GateSearchMode.LockedScale
            && gateContext.LockedScale is { } lockedScale)
        {
            var lockedGoodEnough = gates.Count >= 2
                || (gates.Count == 1
                    && gates[0].Score >= tuning.GateTemplateThreshold
                        + GateTemplateRules.SingleGateAmbiguityGap
                    && Math.Abs((gates[0].Scale / lockedScale) - 1d) <= 0.12d);

            if (!lockedGoodEnough)
            {
                var warmContext = new GateSearchContext
                {
                    Mode = GateSearchMode.WarmScaleSearch,
                    WarmScale = lockedScale,
                    AllowSingleGateEarlyExit = true,
                    SingleGateScoreThreshold =
                        GateTemplateRules.EarlyExitScoreThreshold,
                    SingleGateScaleTolerance =
                        GateTemplateRules.SingleGateScaleTolerance,
                    AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
                };
                if (tuning.WarmGateSearchBudgetMs > 0)
                    warmContext.TimeBudgetMilliseconds =
                        tuning.WarmGateSearchBudgetMs;

                stopwatch.Restart();
                gateResult = _gateDetector.Detect(
                    liveMatchImage,
                    frame.ViewportBounds,
                    frame.ClientBounds.Width,
                    tuning.GateTemplateThreshold,
                    warmContext);
                gates = gateResult.Gates;
                stopwatch.Stop();
                diagnostics.GateDetectionMilliseconds =
                    stopwatch.Elapsed.TotalMilliseconds;

                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Warning,
                    $"LockedScale 单 scale 搜索未提供合格的门候选 " +
                    $"(找到 {gates.Count} 个)，回退到 WarmScaleSearch",
                    elapsedMs: diagnostics.GateDetectionMilliseconds,
                    details: new()
                    {
                        ["fallbackFrom"] = "LockedScale",
                        ["fallbackTo"] = "WarmScaleSearch",
                        ["lockedScale"] = lockedScale,
                        ["gateCount"] = gates.Count,
                    });
            }
        }

        diagnostics.GateCandidateCount = gates.Count;
        diagnostics.GateSearchMode = gateResult.SearchModeUsed;
        diagnostics.GateSearchStopReason = gateResult.StopReason;
        diagnostics.GateScalesEvaluated = gateResult.ScalesEvaluated;
        diagnostics.GateMatchTemplateCalls = gateResult.MatchTemplateCalls;
        diagnostics.GateBudgetExceeded = gateResult.BudgetExceeded;
        MapLogCollector.Instance.Append(MapLogCategory.GateDetection, MapLogLevel.Info,
            $"门检测完成 · {gates.Count} 个候选 · 模式 {gateResult.SearchModeUsed} · 原因 {gateResult.StopReason}",
            elapsedMs: diagnostics.GateDetectionMilliseconds,
            details: new()
            {
                ["gateCount"] = gates.Count,
                ["mode"] = gateResult.SearchModeUsed.ToString(),
                ["stopReason"] = gateResult.StopReason.ToString(),
                ["scalesEvaluated"] = gateResult.ScalesEvaluated,
                ["matchTemplateCalls"] = gateResult.MatchTemplateCalls,
            });
        var dynamicIgnoreRegions = gates
            .Select(gate => ToLocalRect(
                gate.ScreenBounds,
                frame.ViewportBounds,
                frame.Image.Size()))
            .Where(region => region.Width > 0 && region.Height > 0)
            .ToList();

        if (gates.Count >= 2)
        {
            stopwatch.Restart();
            var ranked = MapCvRecognitionScript.RankGeometry(
                [fingerprint],
                gates,
                frame.ViewportBounds,
                tuning.VectorErrorTolerance);
            stopwatch.Stop();
            diagnostics.GeometryMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            if (!TryValidateRanking(ranked, tuning, diagnostics, out var failure))
            {
                if (tuning.ForceBestRecognitionResult
                    && compatibleSession is not null)
                {
                    return ReuseLastTransformAttempt(
                        fingerprint,
                        compatibleSession,
                        diagnostics);
                }
                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return failure!;
            }

            var winner = ranked[0];
            if (!TryBuildRecognition(
                    winner,
                    alignmentMode,
                    tuning,
                    margin: double.PositiveInfinity,
                    usedConfirmation: false,
                    MapRecognitionSource.SelectedMapGatePair,
                    wasForcedBestResult: false,
                    out var recognition,
                    out var transformFailure))
            {
                if (tuning.ForceBestRecognitionResult
                    && compatibleSession is not null)
                {
                    return ReuseLastTransformAttempt(
                        fingerprint,
                        compatibleSession,
                        diagnostics);
                }
                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return Failure(
                    diagnostics,
                    $"双门与已选地图一致，但无法安全对齐覆盖层：{transformFailure}");
            }
            if (recognition!.Result.Confidence < tuning.MinimumConfidence
                && !tuning.ForceBestRecognitionResult)
            {
                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return Failure(
                    diagnostics,
                    $"已选地图的双门对齐置信度 {recognition.Result.Confidence:P0} "
                    + $"低于阈值 {tuning.MinimumConfidence:P0}。");
            }

            if (compatibleSession is not null
                && recognition.Result.OverlayTransform is { } measured)
            {
                var scaleChange = Math.Abs(
                    (measured.ScaleX
                        / compatibleSession.LockedTransform.ScaleX) - 1d);
                if (scaleChange > nativeScaleChangeRatio)
                {
                    diagnostics.TrackingMode =
                        MapAlignmentTrackingMode.NeedsGatePair;
                    diagnostics.StructureRejectionReason =
                        MapStructureRejectionReason.NativeScaleChanged;
                    return Failure(
                        diagnostics,
                        $"双门测得的原生地图缩放与固定标定相差超过 "
                        + $"{nativeScaleChangeRatio:P0}，"
                        + "本次结果已拒绝，需要重新确认地图缩放。");
                }
                if (MapOverlayTransformSolver.TryTranslateWithLockedScale(
                        compatibleSession.LockedTransform,
                        recognition.Result.AnchorMatches,
                        out var fixedScaleTransform,
                        out _))
                {
                    recognition = ReplaceTransform(
                        recognition,
                        fixedScaleTransform);
                }
            }

            if (CanDirectLockGatePair(recognition, tuning))
            {
                recognition = MarkFastEvidence(
                    recognition,
                    MapAlignmentEvidenceKind.DualGate,
                    MapStructureEvidenceDisposition.None,
                    skippedStructure: true);
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    "双门快速锁定，跳过结构复核");
                _gateDetector.RememberSuccessfulScale(
                    (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
                diagnostics.UsedForcedBestResult = false;
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.GatePairLocked;
                diagnostics.AlignmentEvidence =
                    MapAlignmentEvidenceKind.DualGate;
                diagnostics.SkippedStructureValidation = true;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Recognition = recognition,
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                };
            }

            if (!TryValidateAnchorRecognitionWithStructure(
                    fingerprint,
                    frame,
                    recognition,
                    structureTuning,
                    tuning.MinimumConfidence,
                    playerPrior,
                    predictedViewportOrigin,
                    liveIgnoreRegions,
                    dynamicIgnoreRegions,
                    candidateHistory,
                    out var validatedRecognition,
                    out var anchorStructure,
                    out var structureFailure))
            {
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.NeedsGatePair;
                diagnostics.StructureRejectionReason =
                    anchorStructure?.RejectionReason
                    ?? MapStructureRejectionReason.NoCandidate;
                diagnostics.StructureDisposition =
                    diagnostics.StructureRejectionReason.ToDisposition();
                return Failure(
                    diagnostics,
                    $"双门几何已匹配，但静态结构与地图边界复核失败：{structureFailure}");
            }
            recognition = validatedRecognition;
            diagnostics.StructurePreprocessMilliseconds =
                anchorStructure!.PreprocessMilliseconds;
            diagnostics.StructureSearchMilliseconds =
                anchorStructure.SearchMilliseconds;
            diagnostics.StructureRefineMilliseconds =
                anchorStructure.RefineMilliseconds;
            diagnostics.StructureBestScore = anchorStructure.BestScore;
            diagnostics.StructureSecondScore = anchorStructure.SecondScore;
            diagnostics.StructureCandidateMargin =
                anchorStructure.CandidateMargin;
            diagnostics.StructureRejectionReason =
                anchorStructure.RejectionReason;
            diagnostics.StructureDisposition =
                anchorStructure.RejectionReason.ToDisposition(
                    anchorStructure.Accepted);
            diagnostics.AlignmentEvidence =
                MapAlignmentEvidenceKind.Structure;
            PopulateStructureDiagnostics(
                diagnostics,
                anchorStructure);
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                $"结构复核：{(anchorStructure!.Accepted ? "通过" : "未通过")} · 置信度 {anchorStructure.Confidence:P0}",
                elapsedMs: anchorStructure.SearchMilliseconds + anchorStructure.RefineMilliseconds,
                details: new() { ["accepted"] = anchorStructure.Accepted, ["confidence"] = anchorStructure.Confidence, ["bestScore"] = anchorStructure.BestScore, ["rejectionReason"] = anchorStructure.RejectionReason.ToString() });

            _gateDetector.RememberSuccessfulScale(
                (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
            diagnostics.UsedForcedBestResult =
                recognition.Result.WasForcedBestResult;
            diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = recognition,
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
            };
        }

        if (compatibleSession is null)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return Failure(
                diagnostics,
                $"已保留 {fingerprint.Map.DisplayName}，但本次运行尚未完成双门缩放锁定；"
                + "请让大门和侧门同时出现在地图显示边界内一次。");
        }
        session = compatibleSession;

        using var reference = Cv2.ImRead(
            fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
        if (reference.Empty())
        {
            if (tuning.ForceBestRecognitionResult)
            {
                return ReuseLastTransformAttempt(
                    fingerprint,
                    session,
                    diagnostics);
            }
            diagnostics.TrackingMode = MapAlignmentTrackingMode.WaitingForAnchor;
            return Failure(diagnostics, "无法读取当前所选地图的识别区域。");
        }

        string? singleGateFallbackReason = null;
        RuntimeMapRecognition? singleGateProposal = null;
        var structureSeed = session.LockedTransform;
        if (gates.Count == 1)
        {
            var gate = gates[0];
            if (session.GateTemplateScale is { } lockedGateScale
                && Math.Abs((gate.Scale / lockedGateScale) - 1d) > 0.12d)
            {
                if (tuning.ForceBestRecognitionResult)
                {
                    return ReuseLastTransformAttempt(
                        fingerprint,
                        session,
                        diagnostics);
                }
                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return Failure(
                    diagnostics,
                    "单门尺寸与已锁定缩放不一致，可能发生了地图缩放；请等待双门重新锁定。");
            }

            stopwatch.Restart();
            var resolved = MapAnchorTracker.TryResolveSingleGate(
                reference,
                frame.Image,
                fingerprint,
                gate,
                frame.ViewportBounds,
                session.LockedTransform,
                tuning.MinimumConfidence,
                tuning.ConfirmationAdvantage,
                out var evidence,
                out var identityFailure);
            stopwatch.Stop();
            diagnostics.ConfirmationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            MapLogCollector.Instance.Append(MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"单门身份识别{(resolved ? "成功" : "失败")} · {stopwatch.Elapsed.TotalMilliseconds:F0}ms",
                elapsedMs: stopwatch.Elapsed.TotalMilliseconds);
            if (!resolved)
            {
                singleGateFallbackReason = identityFailure;
            }
            else if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                         session.LockedTransform,
                         [evidence],
                         out var transform,
                         out var transformFailure))
            {
                singleGateFallbackReason = transformFailure;
            }
            else
            {
                diagnostics.TrackingMode = MapAlignmentTrackingMode.SingleGateTracking;

                // 区分常规单门跟踪和侧门扫描后的单门验证
                double singleGateConfidence;
                if (session.SideEntranceScanPriorConfidence > 0d)
                {
                    // 侧门扫描模式：地图ID已知，单门用于位置验证
                    var scaleAgreement = MapAlignmentConfidence.ComputeScaleAgreement(
                        gate.Scale,
                        session.BaselineGateScale);
                    singleGateConfidence = MapAlignmentConfidence
                        .ComputeSideEntranceSingleGateConfidence(
                            session.SideEntranceScanPriorConfidence,
                            evidence.Score,
                            scaleAgreement);
                }
                else
                {
                    // 常规单门跟踪：基于双门几何锁定
                    var scaleAgreement = MapAlignmentConfidence.ComputeScaleAgreement(
                        gate.Scale,
                        session.GateTemplateScale ?? session.BaselineGateScale);
                    singleGateConfidence = MapAlignmentConfidence
                        .ComputeSingleGateTrackingConfidence(
                            evidence.Score,
                            session.LastConfidence,
                            scaleAgreement);
                }

                singleGateProposal = BuildTrackedRecognition(
                    fingerprint,
                    transform,
                    [evidence],
                    MapRecognitionSource.SingleGateTracking,
                    confidenceOverride: singleGateConfidence,
                    evidenceKind:
                        MapAlignmentEvidenceKind.None);
                structureSeed = transform;
            }

            diagnostics.UsedSingleGateStructureFallback =
                singleGateProposal is null;
            diagnostics.SingleGateFallbackReason =
                singleGateFallbackReason ?? string.Empty;
        }

        if (alignmentMode != MapOverlayAlignmentMode.Uniform)
        {
            if (tuning.ForceBestRecognitionResult)
            {
                return ReuseLastTransformAttempt(
                    fingerprint,
                    session,
                    diagnostics);
            }
            diagnostics.TrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
            return Failure(
                diagnostics,
                (singleGateFallbackReason is null
                    ? "两扇门都不可见"
                    : $"{singleGateFallbackReason}；单门无法安全更新平移")
                + "，而结构配准只支持等比缩放；当前 XY 分别缩放模式已保留上次对齐。");
        }

        MapAuxiliaryTrackingResult? auxiliary = null;
        if (structureTuning.UseAuxiliaryAnchorRecognition)
        {
            auxiliary = MapAnchorTracker.TrackAuxiliaryAnchors(
                reference,
                frame.Image,
                fingerprint,
                frame.ViewportBounds,
                session.LockedTransform,
                tuning.GateTemplateThreshold,
                tuning.ConfirmationAdvantage,
                structureTuning.MaximumAuxiliaryTemplates,
                _auxiliaryTemplateCache);
            diagnostics.AuxiliaryAnchorMilliseconds =
                auxiliary.SearchMilliseconds;
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"辅助锚点追踪{(auxiliary.IsSuccess ? "成功" : "失败")} · "
                + $"{auxiliary.Matches.Count} 个匹配 · "
                + $"{auxiliary.SearchMilliseconds:F0}ms",
                elapsedMs: auxiliary.SearchMilliseconds);
            diagnostics.AuxiliaryAnchorMatchCount = auxiliary.Matches.Count;
            diagnostics.AuxiliaryTemplatesEvaluated =
                auxiliary.TemplatesEvaluated;
            diagnostics.AuxiliaryUsedGlobalSearch =
                auxiliary.UsedGlobalSearch;
            diagnostics.AuxiliaryConfidence = auxiliary.Confidence;
            dynamicIgnoreRegions.AddRange(
                auxiliary.Matches
                    .Select(match => ToLocalRect(
                        match.ScreenBounds,
                        frame.ViewportBounds,
                        frame.Image.Size()))
                    .Where(region =>
                        region.Width > 0 && region.Height > 0));
            if (auxiliary.IsSuccess
                && MapOverlayTransformSolver.TryTranslateWithLockedScale(
                    session.LockedTransform,
                    auxiliary.Matches,
                    out var proposedSeed,
                    out _))
            {
                structureSeed = proposedSeed;
            }

            if (TryBuildDirectAuxiliaryRecognition(
                    fingerprint,
                    session,
                    singleGateProposal,
                    auxiliary,
                    frame.ViewportBounds,
                    structureTuning.AuxiliaryDirectLockConfidence,
                    out var auxiliaryRecognition))
            {
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.AuxiliaryAnchorTracking;
                diagnostics.AlignmentEvidence =
                    auxiliaryRecognition!.Result.EvidenceKind;
                diagnostics.SkippedStructureValidation = true;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Recognition = auxiliaryRecognition,
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                };
            }
        }

        dynamicIgnoreRegions.AddRange(
            BuildProjectedOutsideIgnoreRegions(
                fingerprint.Map,
                fingerprint.FloorKey,
                frame,
                structureSeed));
        var primaryProfile = MapFloorRules.GetFloorProfile(
            fingerprint.Map,
            fingerprint.FloorKey) ?? fingerprint.Map.Recognition.FirstFloor;
        stopwatch.Restart();
        using var preparedReference = _structureCache.GetOrCreate(
            fingerprint.Map.Id,
            fingerprint.Map.UpdatedAt,
            reference,
            primaryProfile.WholeImageIgnoreRegions,
            fingerprint.FloorKey);
        stopwatch.Stop();
        diagnostics.CacheMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        using var preparedLive = _structurePreprocessor.ProcessLiveRoi(
            frame.Image,
            liveIgnoreRegions,
            dynamicIgnoreRegions);
        stopwatch.Stop();
        diagnostics.StructurePreprocessMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;
        var fastStructureTuning = structureTuning.Clone();
        fastStructureTuning.TopCandidateCount = Math.Min(
            3,
            fastStructureTuning.TopCandidateCount);
        var hasAnchorSeed = singleGateProposal is not null
            || auxiliary?.IsSuccess is true;
        var structure = _structureRegistrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = structureSeed,
                Tuning = fastStructureTuning,
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = hasAnchorSeed,
                TrackingMode = true,
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                FixedRotationDegrees = primaryProfile.OrientationDegrees,
                ValidMapBounds = primaryProfile.GetEffectiveValidMapBounds(),
                PlayerPrior = playerPrior,
                PredictedViewportOrigin = predictedViewportOrigin,
                LiveIgnoreRegions = liveIgnoreRegions ?? [],
                DynamicIgnoreRegions = dynamicIgnoreRegions,
                CandidateHistory = candidateHistory ?? [],
                SideEntrancePrior = session.SideEntranceScanPriorConfidence
            });
        WriteStructureDebugResult(
            fingerprint.Map,
            structure,
            singleGateFallbackReason);
        diagnostics.StructureSearchMilliseconds =
            structure.SearchMilliseconds;
        diagnostics.StructureRefineMilliseconds =
            structure.RefineMilliseconds;
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence =
            MapAlignmentEvidenceKind.Structure;
        PopulateStructureDiagnostics(diagnostics, structure);

        // 结构配准置信度已在 MapStructureConfidenceCalculator 中原生处理侧门先验
        var effectiveStructureConfidence = structure.Confidence;

        var postStructureTimer = Stopwatch.StartNew();
        if (!structure.Accepted
            || structure.Transform is null
            || (effectiveStructureConfidence < tuning.MinimumConfidence
                && !tuning.ForceBestRecognitionResult))
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
            if (tuning.ForceBestRecognitionResult)
            {
                diagnostics.UsedForcedBestResult = true;
                diagnostics.StructureAttempted = true;
                diagnostics.StructureAccepted = false;
                diagnostics.StructureFailureReason =
                    structure.FailureReason;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    StructureResult = structure,
                    Recognition = BuildReusedTransformRecognition(
                        fingerprint,
                        session,
                        structure),
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                    StructureAttempted = true,
                    StructureAccepted = false,
                    StructureFailureReason = structure.FailureReason,
                };
            }
            var failureReason = structure.Accepted
                && structure.Confidence < tuning.MinimumConfidence
                    ? $"结构配准置信度 {structure.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}"
                    : structure.FailureReason;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = failureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason =
                    (singleGateFallbackReason is null
                        ? string.Empty
                        : $"{singleGateFallbackReason}；已回退结构配准，但")
                    + $"{failureReason}；已保留最后可靠对齐，等待下次开图恢复。",
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = failureReason,
            };
        }

        var gateBaseline = session.BaselineGateScale > 0d
            ? session.BaselineGateScale
            : session.LockedTransform.ScaleX;
        if (Math.Abs((structure.Transform.ScaleX / gateBaseline) - 1d)
                > structureTuning.ScaleSearchRadius + 0.0001d
            && !tuning.ForceBestRecognitionResult)
        {
            var rejected = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.ScaleChangeTooLarge,
                candidates: structure.Candidates,
                preprocessMilliseconds: structure.PreprocessMilliseconds,
                searchMilliseconds: structure.SearchMilliseconds,
                debugOutputDirectory: structure.DebugOutputDirectory);
            diagnostics.TrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
            diagnostics.StructureRejectionReason = rejected.RejectionReason;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = rejected.FailureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = rejected,
                FailureReason =
                    $"{rejected.FailureReason}；已保留最后可靠对齐，等待双门重新锁定。",
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = rejected.FailureReason,
            };
        }

        diagnostics.TrackingMode = singleGateProposal is null
            ? MapAlignmentTrackingMode.StructureMatched
            : MapAlignmentTrackingMode.SingleGateTracking;
        diagnostics.UsedForcedBestResult =
            tuning.ForceBestRecognitionResult
            && (structure.WasForcedBestCandidate
                || structure.Confidence < tuning.MinimumConfidence);
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = structure.Accepted;
        diagnostics.StructureFailureReason =
            structure.Accepted ? string.Empty : structure.FailureReason;
        postStructureTimer.Stop();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构后处理完成 · {postStructureTimer.Elapsed.TotalMilliseconds:F0}ms",
            elapsedMs: postStructureTimer.Elapsed.TotalMilliseconds);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = BuildStructureRecognition(
                fingerprint,
                structure.Transform,
                structure,
                diagnostics.UsedForcedBestResult,
                singleGateProposal),
            GateDetectionResult = gateResult,
            SearchStage = diagnostics.SearchStage,
            StructureAttempted = true,
            StructureAccepted = structure.Accepted,
            StructureFailureReason =
                structure.Accepted ? string.Empty : structure.FailureReason,
        };
    }

    /// <summary>
    /// Aligns one exact non-primary floor from its own static structure.  This
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
        bool useProjectedBoundaryMask = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = NormalizedCopy(tuning);
        tuning.ForceBestRecognitionResult = false;
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        structureTuning ??= new MapStructureRegistrationTuning();
        structureTuning = structureTuning.Clone();
        structureTuning.Normalize();
        var diagnostics = CreateDiagnostics();

        var map = _maps.FirstOrDefault(candidate => candidate.Id == selectedMapId);
        if (map is null)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return Failure(
                diagnostics,
                "当前选择的地图不存在或未加载。");
        }
        if (MapFloorRules.UsesDoubleGateAlignment(map, floorKey))
            return Failure(diagnostics, "The primary floor must use double-gate alignment.");
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (profile is null)
            return Failure(diagnostics, $"The selected map does not contain floor '{floorKey}'.");
        if (!double.IsFinite(scaleSeed.ScaleX)
            || scaleSeed.ScaleX <= 0.05d)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return Failure(
                diagnostics,
                $"Floor '{floorKey}' has no valid primary scale seed.");
        }
        if (profile.OrientationDegrees != 0)
        {
            return Failure(
                diagnostics,
                $"Floor '{floorKey}' structure alignment requires 0-degree orientation.");
        }
        var referencePath = _repository.GetFloorRecognitionPath(map, floorKey);
        if (!File.Exists(referencePath))
        {
            return Failure(
                diagnostics,
                $"The recognition image for floor '{floorKey}' is missing.");
        }

        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        if (reference.Empty())
        {
            return Failure(diagnostics, $"The recognition image for floor '{floorKey}' cannot be read.");
        }

        IReadOnlyList<Rect> dynamicIgnoreRegions = useProjectedBoundaryMask
            ? BuildProjectedOutsideIgnoreRegions(map, floorKey, frame, scaleSeed)
            : [];
        var stopwatch = Stopwatch.StartNew();
        using var preparedReference = _structureCache.GetOrCreate(
            map.Id,
            map.UpdatedAt,
            reference,
            profile.WholeImageIgnoreRegions,
            floorKey);
        stopwatch.Stop();
        diagnostics.CacheMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        using var preparedLive = _structurePreprocessor.ProcessLiveRoi(
            frame.Image,
            liveIgnoreRegions,
            dynamicIgnoreRegions);
        stopwatch.Stop();
        diagnostics.StructurePreprocessMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;

        var structure = _structureRegistrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = scaleSeed,
                Tuning = structureTuning,
                AllowScaleSearch = true,
                RestrictSearchToLockedTransform = false,
                TrackingMode = isTracking,
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                FixedRotationDegrees = profile.OrientationDegrees,
                ValidMapBounds = profile.GetEffectiveValidMapBounds(),
                PlayerPrior = playerPrior,
                PredictedViewportOrigin = predictedViewportOrigin,
                LiveIgnoreRegions = liveIgnoreRegions ?? [],
                DynamicIgnoreRegions = dynamicIgnoreRegions,
                CandidateHistory = candidateHistory ?? []
            });
        WriteStructureDebugResult(map, structure, null);
        PopulateStructureDiagnostics(diagnostics, structure);
        diagnostics.StructureSearchMilliseconds =
            structure.SearchMilliseconds;
        diagnostics.StructureRefineMilliseconds =
            structure.RefineMilliseconds;
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;

        if (!structure.Accepted
            || structure.Transform is null
            || structure.Confidence < tuning.MinimumConfidence)
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = structure.FailureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason =
                    $"{structure.FailureReason}; floor '{floorKey}' alignment was not locked.",
                SearchStage = AlignmentSearchStage.StructureFallback,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = structure.FailureReason,
            };
        }
        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.StructureMatched;
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = true;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = BuildFloorStructureRecognition(
                map,
                floorKey,
                _repository.GetFloorOverlayPath(map, floorKey),
                structure.Transform,
                structure),
            SearchStage = AlignmentSearchStage.StructureFallback,
            StructureAttempted = true,
            StructureAccepted = true,
            StructureFailureReason = string.Empty,
        };
    }

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
        string? mapClass = null)
    {
        var previousTransform = previousAttempt
            .Recognition?.Result.OverlayTransform;
        var previousGates = previousAttempt
            .GateDetectionResult?.Gates ?? [];

        var predictedRegions = previousGates
            .Select(g => g.ScreenBounds)
            .ToList();

        var predictedScale = previousGates.Count > 0
            ? previousGates.Average(g => g.Scale)
            : (double?)null;

        var gateContext = new GateSearchContext
        {
            Mode = GateSearchMode.LocalConfirmationSearch,
            PredictedGateRegions = predictedRegions,
            PredictedScale = predictedScale,
            LocalRoiTemplatePaddingFactor =
                tuning.ConfirmationRoiTemplatePaddingFactor,
            LocalRoiMinimumPaddingPixels =
                tuning.ConfirmationRoiMinimumPaddingPixels,
            MaximumExpectedMotionPixels =
                tuning.ConfirmationMaximumMotionPixels,
        };

        if (tuning.ConfirmationGateSearchBudgetMs > 0)
            gateContext.TimeBudgetMilliseconds =
                tuning.ConfirmationGateSearchBudgetMs;

        var alignmentContext = new AlignmentSearchContext
        {
            GateSearch = gateContext,
            PreviousAttempt = previousAttempt,
            ExpectedTransform = previousTransform is { } t
                ? MapSimilarityTransform.FromOverlay(t)
                : null,
            UseRestrictedStructureFallback = true,
            RequireCurrentFrameEvidence = true,
            AllowFullSearchUpgrade = previousGates.Count == 1,
        };

        return AlignSelected(
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
            alignmentSearchContext: alignmentContext,
            nativeScaleChangeRatio: nativeScaleChangeRatio,
            mapClass: mapClass);
    }

    private bool TryValidateAnchorRecognitionWithStructure(
        MapGeometryFingerprint fingerprint,
        CapturedGameFrame frame,
        RuntimeMapRecognition anchorRecognition,
        MapStructureRegistrationTuning structureTuning,
        double minimumConfidence,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<Rect> dynamicIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        out RuntimeMapRecognition validatedRecognition,
        out MapStructureRegistrationResult? structure,
        out string failureReason)
    {
        validatedRecognition = anchorRecognition;
        structure = null;
        failureReason = string.Empty;
        if (anchorRecognition.Result.OverlayTransform
            is not { } anchorTransform)
        {
            failureReason = "双门结果没有有限的相似变换。";
            return false;
        }

        using var reference = Cv2.ImRead(
            fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
        if (reference.Empty())
        {
            failureReason = "无法读取当前地图的主层识别图。";
            return false;
        }

        using var preparedReference = _structureCache.GetOrCreate(
            fingerprint.Map.Id,
            fingerprint.Map.UpdatedAt,
            reference,
            (MapFloorRules.GetFloorProfile(
                fingerprint.Map,
                fingerprint.FloorKey) ?? fingerprint.Map.Recognition.FirstFloor)
                .WholeImageIgnoreRegions,
            fingerprint.FloorKey);
        var effectiveDynamicIgnoreRegions = dynamicIgnoreRegions
            .Concat(BuildProjectedOutsideIgnoreRegions(
                fingerprint,
                frame,
                anchorTransform))
            .Distinct()
            .ToArray();
        using var preparedLive = _structurePreprocessor.ProcessLiveRoi(
            frame.Image,
            liveIgnoreRegions,
            effectiveDynamicIgnoreRegions);
        var validationTuning = structureTuning.Clone();
        validationTuning.ScaleSearchRadius = 0d;
        validationTuning.TopCandidateCount = Math.Min(
            5,
            validationTuning.TopCandidateCount);
        validationTuning.PreviousAlignmentSearchRadiusPixels = Math.Max(
            8,
            (int)Math.Ceiling(8d * anchorTransform.ScaleX));
        structure = _structureRegistrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = anchorTransform,
                Tuning = validationTuning,
                AllowScaleSearch = false,
                RestrictSearchToLockedTransform = true,
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                FixedRotationDegrees = (MapFloorRules.GetFloorProfile(
                    fingerprint.Map,
                    fingerprint.FloorKey) ?? fingerprint.Map.Recognition.FirstFloor)
                    .OrientationDegrees,
                ValidMapBounds = (MapFloorRules.GetFloorProfile(
                    fingerprint.Map,
                    fingerprint.FloorKey) ?? fingerprint.Map.Recognition.FirstFloor)
                    .GetEffectiveValidMapBounds(),
                PlayerPrior = playerPrior,
                PredictedViewportOrigin = predictedViewportOrigin,
                LiveIgnoreRegions = liveIgnoreRegions ?? [],
                DynamicIgnoreRegions = effectiveDynamicIgnoreRegions,
                CandidateHistory = candidateHistory ?? []
            });
        if (!structure.Accepted
            || structure.Transform is null
            || structure.Confidence < minimumConfidence)
        {
            failureReason = structure.Accepted
                ? $"结构置信度 {structure.Confidence:P0} 低于 {minimumConfidence:P0}。"
                : structure.FailureReason;
            return false;
        }

        var structureTransform = structure.Transform;
        var maximumFineCorrection = Math.Max(
            3d,
            validationTuning.PreviousAlignmentSearchRadiusPixels);
        if (Math.Abs(
                structureTransform.OffsetX
                - anchorTransform.OffsetX) > maximumFineCorrection
            || Math.Abs(
                structureTransform.OffsetY
                - anchorTransform.OffsetY) > maximumFineCorrection
            || Math.Abs(
                (structureTransform.ScaleX / anchorTransform.ScaleX)
                - 1d) > 0.003d)
        {
            failureReason =
                "结构精修超出双门候选允许的局部平移范围。";
            structure = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.AnchorTransformConflict,
                failureReason,
                structure.Candidates,
                structure.PreprocessMilliseconds,
                structure.SearchMilliseconds,
                structure.DebugOutputDirectory,
                structure.LockedScale,
                structure.ReferenceWidth,
                structure.ReferenceHeight,
                structure.QueryEdgePixels,
                new Rect(
                    structure.QueryBoundsX,
                    structure.QueryBoundsY,
                    structure.QueryBoundsWidth,
                    structure.QueryBoundsHeight),
                structure.ScaleHypothesisCount,
                structure.OversizedHypothesisCount,
                structure.UsedRestrictedSearch);
            return false;
        }

        var confidence = new MapRegistrationConfidenceEvidence
        {
            AnchorGeometry = anchorRecognition.Result.Confidence,
            StructureQuality = structure.Confidence,
            CandidateSeparation = structure.CandidateMargin,
            BoundsAndPrior = 1d
        }.Calculate();
        validatedRecognition = new RuntimeMapRecognition
        {
            Map = anchorRecognition.Map,
            FloorImagePath = anchorRecognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = anchorRecognition.Result.MapId,
                Floor = anchorRecognition.Result.Floor,
                OrientationDegrees =
                    anchorRecognition.Result.OrientationDegrees,
                Confidence = confidence,
                Source = anchorRecognition.Result.Source,
                HasAllRequiredAnchorEvidence =
                    anchorRecognition.Result.HasAllRequiredAnchorEvidence,
                GeometryMargin =
                    anchorRecognition.Result.GeometryMargin,
                UsedLocalConfirmation = true,
                OverlayTransform = structureTransform,
                AnchorMatches =
                    anchorRecognition.Result.AnchorMatches,
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin =
                    structure.CandidateMargin,
                StructureRejectionReason =
                    structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    MapStructureEvidenceDisposition.Supportive,
                WasForcedBestResult = false
            }
        };
        return true;
    }

    private static void PopulateStructureDiagnostics(
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult structure)
    {
        diagnostics.StructureCandidateCount =
            structure.Candidates.Count;
        diagnostics.StructureFeatureMatchCount =
            structure.FeatureMatchCount;
        diagnostics.StructureFeatureInlierCount =
            structure.FeatureInlierCount;
        diagnostics.StructureFeatureConsensus =
            structure.FeatureConsensus;
        diagnostics.StructureEccConverged =
            structure.EccConverged;
        diagnostics.StructureEccCorrelation =
            structure.EccCorrelation;
        if (structure.ConfidenceBreakdown is { } breakdown)
        {
            diagnostics.StructureGeometricFitQuality =
                breakdown.GeometricFitQuality;
            diagnostics.StructureEvidenceConfidence =
                breakdown.EvidenceConfidence;
            diagnostics.StructureGeometricLockConfidence =
                breakdown.GeometricLockConfidence;
            diagnostics.StructureLockConfidence =
                breakdown.LockConfidence;
            diagnostics.StructureLowEvidenceReason =
                breakdown.LowEvidenceReason;
            diagnostics.StructureHardGateFailure =
                breakdown.HardGateFailure;
        }
        diagnostics.VisibleMaskMs =
            structure.VisibleMaskMilliseconds;
        diagnostics.VisibleFraction =
            structure.VisibleFraction;
        diagnostics.VisibleStructurePixels =
            structure.VisibleStructurePixels;
        diagnostics.VisibleEdgePixels =
            structure.VisibleEdgePixels;
        diagnostics.VisibleAwareSearchMs =
            structure.VisibleAwareSearchMilliseconds;
        diagnostics.VisibleAwareCandidateCount =
            structure.VisibleAwareCandidateCount;
        diagnostics.VisibleAwareTopCost =
            structure.VisibleAwareTopCost;
        diagnostics.VisibleAwareTopMargin =
            structure.VisibleAwareTopMargin;
        diagnostics.VisibleAwareEarlyAccepted =
            structure.VisibleAwareEarlyAccepted;
        diagnostics.VisibleAwareFallbackReason =
            structure.VisibleAwareFallbackReason;
        diagnostics.StructureFastStrategyUsed =
            structure.UsedFastStrategy;
        diagnostics.StructureCoarseSearchMs =
            structure.FastCoarseSearchMilliseconds;
        diagnostics.StructureCoarseCandidateCount =
            structure.FastCoarseCandidateCount;
    }

    public MapRecognitionAttempt RecognizeManual(
        MapScreenRect viewportBounds,
        MapScreenRect mainGateBounds,
        MapScreenRect sideGateBounds,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        string? mapClass = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        tuning = NormalizedCopy(tuning);
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        var diagnostics = CreateDiagnostics();
        diagnostics.GateCandidateCount = 2;
        var fingerprints = FilterFingerprints(mapClass);
        if (fingerprints.Count == 0)
            return Failure(diagnostics, "没有已完成主层区域、大门和侧门标记的地图。");
        if (!viewportBounds.IsValid || !mainGateBounds.IsValid || !sideGateBounds.IsValid)
            return Failure(diagnostics, "手动框选的地图区域或门矩形无效。");

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
        if (!TryValidateRanking(ranked, tuning, diagnostics, out var failure))
        {
            // 即使不满足排名门槛，玩家手动框了门就值得展示候选供选择
            var rescueChoices = BuildChoices(
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

        var margin = GeometryMargin(ranked);
        var choices = BuildChoices(
            ranked, alignmentMode, tuning, margin,
            MapRecognitionSource.ManualGateSelection);
        if (choices.Count == 0)
            return Failure(diagnostics, "手动双门坐标无法生成安全的无旋转缩放与位移。");

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
                Recognition = MarkForcedBestResult(winner)
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

    private IReadOnlyList<MapGeometryFingerprint> FilterFingerprints(string? mapClass)
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
                ReusedLastTransform = result.ReusedLastTransform
            }
        };
    }

    private bool TryBuildRecognition(
        MapGeometryCandidate winner,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        bool usedConfirmation,
        MapRecognitionSource source,
        bool wasForcedBestResult,
        out RuntimeMapRecognition? recognition,
        out string failureReason)
    {
        recognition = null;
        if (!MapOverlayTransformSolver.TrySolve(
                winner,
                alignmentMode,
                out var transform,
                out failureReason))
        {
            return false;
        }

        var fingerprint = winner.Fingerprint;
        var map = fingerprint.Map;
        var profile = MapFloorRules.GetFloorProfile(map, fingerprint.FloorKey)
            ?? map.Recognition.FirstFloor;
        var mainAnchor = profile.FindAnchor("main-entrance")!;
        var sideAnchor = profile.FindAnchor("side-entrance")!;
        // Gate score is the primary confidence driver; geometry is a soft
        // secondary check (see MapAlignmentConfidence.ComputeDualGateConfidence).
        var confidence = MapAlignmentConfidence.ComputeDualGateConfidence(
            winner.MainGate.Score,
            winner.SideGate.Score,
            winner.VectorError,
            tuning.VectorErrorTolerance);
        recognition = new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = confidence,
                Source = source,
                HasAllRequiredAnchorEvidence = true,
                GeometryMargin = double.IsPositiveInfinity(margin) ? 1d : Math.Max(0d, margin),
                UsedLocalConfirmation = usedConfirmation,
                OverlayTransform = transform,
                WasForcedBestResult = wasForcedBestResult
                    || (tuning.ForceBestRecognitionResult
                        && confidence < tuning.MinimumConfidence),
                AnchorMatches =
                [
                    CreateEvidence(mainAnchor, winner.MainGate, fingerprint),
                    CreateEvidence(sideAnchor, winner.SideGate, fingerprint)
                ],
                EvidenceKind = MapAlignmentEvidenceKind.DualGate
            }
        };
        failureReason = string.Empty;
        return true;
    }

    private static bool CanDirectLockGatePair(
        RuntimeMapRecognition recognition,
        MapRecognitionTuning tuning) =>
        MapFastAlignmentRules.CanDirectLockDualGate(
            recognition.Result,
            tuning);

    private static bool TryBuildDirectAuxiliaryRecognition(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        RuntimeMapRecognition? singleGateProposal,
        MapAuxiliaryTrackingResult auxiliary,
        MapScreenRect viewportBounds,
        double auxiliaryDirectLockConfidence,
        out RuntimeMapRecognition? recognition)
    {
        recognition = null;
        IReadOnlyList<CvAnchorEvidence> matches;
        MapAlignmentEvidenceKind evidenceKind;
        double confidence;
        if (auxiliary.HasIndependentConsensus
            && auxiliary.Confidence >= auxiliaryDirectLockConfidence)
        {
            matches = auxiliary.Matches;
            confidence = auxiliary.Confidence;
            evidenceKind = MapAlignmentEvidenceKind.AuxiliaryConsensus;
        }
        else if (singleGateProposal is not null
            && auxiliary.Matches.Count > 0)
        {
            matches = singleGateProposal.Result.AnchorMatches
                .Concat(auxiliary.Matches.Take(1))
                .DistinctBy(match => match.AnchorId)
                .ToArray();
            if (matches.Count < 2 || matches.Any(match => match.Score < 0.78d))
                return false;
            var referenceDiagonal = Math.Sqrt(
                (fingerprint.ReferenceWidth * fingerprint.ReferenceWidth)
                + (fingerprint.ReferenceHeight * fingerprint.ReferenceHeight));
            if (Distance(
                    new Point2d(
                        matches[0].ReferenceBounds.CenterX,
                        matches[0].ReferenceBounds.CenterY),
                    new Point2d(
                        matches[1].ReferenceBounds.CenterX,
                        matches[1].ReferenceBounds.CenterY))
                < referenceDiagonal * 0.05d)
            {
                return false;
            }
            confidence = Math.Clamp(
                matches.Average(match => match.Score),
                0d,
                1d);
            if (confidence < auxiliaryDirectLockConfidence)
                return false;
            evidenceKind =
                MapAlignmentEvidenceKind.SingleGateAndAuxiliary;
        }
        else
        {
            return false;
        }

        if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                session.LockedTransform,
                matches,
                out var transform,
                out _))
        {
            return false;
        }
        var tolerance = Math.Max(
            6d,
            Math.Sqrt(
                (viewportBounds.Width * viewportBounds.Width)
                + (viewportBounds.Height * viewportBounds.Height))
            * 0.005d);
        if (transform.MaximumResidualPixels > tolerance)
            return false;

        recognition = BuildTrackedRecognition(
            fingerprint,
            transform,
            matches,
            MapRecognitionSource.AuxiliaryAnchorTracking,
            confidence,
            evidenceKind);
        return true;
    }

    private static RuntimeMapRecognition MarkFastEvidence(
        RuntimeMapRecognition recognition,
        MapAlignmentEvidenceKind evidenceKind,
        MapStructureEvidenceDisposition structureDisposition,
        bool skippedStructure) =>
        new()
        {
            Map = recognition.Map,
            FloorImagePath = recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = recognition.Result.MapId,
                Floor = recognition.Result.Floor,
                OrientationDegrees =
                    recognition.Result.OrientationDegrees,
                Confidence = recognition.Result.Confidence,
                Source = recognition.Result.Source,
                HasAllRequiredAnchorEvidence =
                    recognition.Result.HasAllRequiredAnchorEvidence,
                GeometryMargin = recognition.Result.GeometryMargin,
                UsedLocalConfirmation =
                    recognition.Result.UsedLocalConfirmation,
                OverlayTransform = recognition.Result.OverlayTransform,
                AnchorMatches = recognition.Result.AnchorMatches,
                StructureBestScore =
                    recognition.Result.StructureBestScore,
                StructureSecondScore =
                    recognition.Result.StructureSecondScore,
                StructureCandidateMargin =
                    recognition.Result.StructureCandidateMargin,
                StructureRejectionReason =
                    recognition.Result.StructureRejectionReason,
                WasForcedBestResult =
                    recognition.Result.WasForcedBestResult,
                ReusedLastTransform =
                    recognition.Result.ReusedLastTransform,
                EvidenceKind = evidenceKind,
                StructureDisposition = structureDisposition,
                SkippedStructureValidation = skippedStructure
            }
        };

    private static RuntimeMapRecognition BuildTrackedRecognition(
        MapGeometryFingerprint fingerprint,
        MapOverlayTransform transform,
        IReadOnlyList<CvAnchorEvidence> matches,
        MapRecognitionSource source,
        double? confidenceOverride = null,
        MapAlignmentEvidenceKind evidenceKind =
            MapAlignmentEvidenceKind.None)
    {
        var confidence = confidenceOverride ?? (matches.Count == 0
            ? 0d
            : Math.Clamp(matches.Average(match => match.Score), 0d, 1d));
        return new RuntimeMapRecognition
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = confidence,
                Source = source,
                HasAllRequiredAnchorEvidence = false,
                GeometryMargin = 0d,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches = matches,
                EvidenceKind = evidenceKind,
                StructureDisposition =
                    MapStructureEvidenceDisposition.None,
                SkippedStructureValidation = true
            }
        };
    }

    private static RuntimeMapRecognition ReplaceTransform(
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform) =>
        new()
        {
            Map = recognition.Map,
            FloorImagePath = recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = recognition.Result.MapId,
                Floor = recognition.Result.Floor,
                OrientationDegrees = recognition.Result.OrientationDegrees,
                Confidence = recognition.Result.Confidence,
                Source = recognition.Result.Source,
                HasAllRequiredAnchorEvidence =
                    recognition.Result.HasAllRequiredAnchorEvidence,
                GeometryMargin = recognition.Result.GeometryMargin,
                UsedLocalConfirmation =
                    recognition.Result.UsedLocalConfirmation,
                OverlayTransform = transform,
                AnchorMatches = recognition.Result.AnchorMatches,
                StructureBestScore = recognition.Result.StructureBestScore,
                StructureSecondScore =
                    recognition.Result.StructureSecondScore,
                StructureCandidateMargin =
                    recognition.Result.StructureCandidateMargin,
                StructureRejectionReason =
                    recognition.Result.StructureRejectionReason,
                WasForcedBestResult =
                    recognition.Result.WasForcedBestResult,
                ReusedLastTransform =
                    recognition.Result.ReusedLastTransform,
                EvidenceKind = recognition.Result.EvidenceKind,
                StructureDisposition =
                    recognition.Result.StructureDisposition,
                SkippedStructureValidation =
                    recognition.Result.SkippedStructureValidation
            }
        };

    private static Rect ToLocalRect(
        MapScreenRect screen,
        MapScreenRect viewport,
        Size imageSize)
    {
        var left = Math.Clamp(
            (int)Math.Floor(screen.X - viewport.X),
            0,
            Math.Max(0, imageSize.Width - 1));
        var top = Math.Clamp(
            (int)Math.Floor(screen.Y - viewport.Y),
            0,
            Math.Max(0, imageSize.Height - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling(screen.X + screen.Width - viewport.X),
            left + 1,
            imageSize.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(screen.Y + screen.Height - viewport.Y),
            top + 1,
            imageSize.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<Rect> BuildProjectedOutsideIgnoreRegions(
        MapGeometryFingerprint fingerprint,
        CapturedGameFrame frame,
        MapOverlayTransform transform) =>
        BuildProjectedOutsideIgnoreRegions(
            fingerprint.Map,
            fingerprint.FloorKey,
            frame,
            transform);

    private static IReadOnlyList<Rect> BuildProjectedOutsideIgnoreRegions(
        MapRecord map,
        string floor,
        CapturedGameFrame frame,
        MapOverlayTransform transform)
    {
        if (frame.Image.Empty()
            || !double.IsFinite(transform.ScaleX)
            || transform.ScaleX <= 0d)
        {
            return [];
        }
        var bounds = (map.Recognition.GetFloor(floor)
            ?? map.Recognition.FirstFloor)
            .GetEffectiveValidMapBounds();
        var projectedLeft = (bounds.X * transform.ScaleX)
            + transform.OffsetX
            - frame.ViewportBounds.X;
        var projectedTop = (bounds.Y * transform.ScaleY)
            + transform.OffsetY
            - frame.ViewportBounds.Y;
        var projectedRight = (bounds.Right * transform.ScaleX)
            + transform.OffsetX
            - frame.ViewportBounds.X;
        var projectedBottom = (bounds.Bottom * transform.ScaleY)
            + transform.OffsetY
            - frame.ViewportBounds.Y;
        var left = Math.Clamp(
            (int)Math.Floor(projectedLeft),
            0,
            frame.Image.Width);
        var top = Math.Clamp(
            (int)Math.Floor(projectedTop),
            0,
            frame.Image.Height);
        var right = Math.Clamp(
            (int)Math.Ceiling(projectedRight),
            0,
            frame.Image.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(projectedBottom),
            0,
            frame.Image.Height);
        if (right <= left || bottom <= top)
            return [new Rect(0, 0, frame.Image.Width, frame.Image.Height)];

        var regions = new List<Rect>(4);
        if (top > 0)
            regions.Add(new Rect(0, 0, frame.Image.Width, top));
        if (bottom < frame.Image.Height)
        {
            regions.Add(new Rect(
                0,
                bottom,
                frame.Image.Width,
                frame.Image.Height - bottom));
        }
        if (left > 0)
            regions.Add(new Rect(0, top, left, bottom - top));
        if (right < frame.Image.Width)
        {
            regions.Add(new Rect(
                right,
                top,
                frame.Image.Width - right,
                bottom - top));
        }
        return regions;
    }

    private static RuntimeMapRecognition BuildStructureRecognition(
        MapGeometryFingerprint fingerprint,
        MapOverlayTransform transform,
        MapStructureRegistrationResult structure,
        bool wasForcedBestResult,
        RuntimeMapRecognition? anchorProposal = null) =>
        new()
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = anchorProposal is null
                    ? structure.Confidence
                    : new MapRegistrationConfidenceEvidence
                    {
                        AnchorGeometry =
                            anchorProposal.Result.Confidence,
                        StructureQuality = structure.Confidence
                    }.Calculate(),
                Source = anchorProposal?.Result.Source
                    ?? MapRecognitionSource.StructureMatching,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches =
                    anchorProposal?.Result.AnchorMatches ?? [],
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin = structure.CandidateMargin,
                StructureRejectionReason = structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    structure.RejectionReason.ToDisposition(
                        structure.Accepted),
                WasForcedBestResult = wasForcedBestResult
            }
        };

    internal static RuntimeMapRecognition BuildFloorStructureRecognition(
        MapRecord map,
        string floorKey,
        string overlayPath,
        MapOverlayTransform transform,
        MapStructureRegistrationResult structure) =>
        new()
        {
            Map = map,
            FloorImagePath = overlayPath,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floorKey,
                OrientationDegrees =
                    MapFloorRules.GetFloorProfile(map, floorKey)?.OrientationDegrees ?? 0,
                Confidence = structure.Confidence,
                Source = MapRecognitionSource.StructureMatching,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches = [],
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin = structure.CandidateMargin,
                StructureRejectionReason = structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    structure.RejectionReason.ToDisposition(
                        structure.Accepted),
                WasForcedBestResult = false
            }
        };

    private static RuntimeMapRecognition BuildReusedTransformRecognition(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapStructureRegistrationResult? structure) =>
        new()
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = session.LastConfidence,
                Source = MapRecognitionSource.ReusedLastTransform,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = false,
                OverlayTransform = session.LockedTransform,
                StructureBestScore =
                    structure?.BestScore ?? session.LastBestScore,
                StructureSecondScore =
                    structure?.SecondScore ?? session.LastSecondScore,
                StructureCandidateMargin =
                    structure?.CandidateMargin
                    ?? session.LastCandidateMargin,
                StructureRejectionReason =
                    structure?.RejectionReason
                    ?? MapStructureRejectionReason.NoCandidate,
                WasForcedBestResult = true,
                ReusedLastTransform = true,
                EvidenceKind = MapAlignmentEvidenceKind.None,
                StructureDisposition =
                    (structure?.RejectionReason
                        ?? MapStructureRejectionReason.NoCandidate)
                    .ToDisposition()
            }
        };

    private static MapRecognitionAttempt ReuseLastTransformAttempt(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult? structure = null)
    {
        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.HoldingLastTransform;
        diagnostics.UsedForcedBestResult = true;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = BuildReusedTransformRecognition(
                fingerprint,
                session,
                structure)
        };
    }

    private static RuntimeMapRecognition MarkForcedBestResult(
        RuntimeMapRecognition original)
    {
        var result = original.Result;
        return new RuntimeMapRecognition
        {
            Map = original.Map,
            FloorImagePath = original.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = result.OrientationDegrees,
                Confidence = result.Confidence,
                Source = result.Source,
                HasAllRequiredAnchorEvidence =
                    result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = result.OverlayTransform,
                AnchorMatches = result.AnchorMatches,
                StructureBestScore = result.StructureBestScore,
                StructureSecondScore = result.StructureSecondScore,
                StructureCandidateMargin =
                    result.StructureCandidateMargin,
                StructureRejectionReason =
                    result.StructureRejectionReason,
                WasForcedBestResult = true,
                ReusedLastTransform = result.ReusedLastTransform,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation =
                    result.SkippedStructureValidation
            }
        };
    }

    private static void WriteStructureDebugResult(
        MapRecord map,
        MapStructureRegistrationResult result,
        string? singleGateFallbackReason)
    {
        if (string.IsNullOrWhiteSpace(result.DebugOutputDirectory))
            return;
        try
        {
            File.WriteAllText(
                Path.Combine(result.DebugOutputDirectory, "result.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        MapId = map.Id,
                        map.SequenceNumber,
                        result.Accepted,
                        Scale = result.Transform?.ScaleX,
                        result.Transform?.OffsetX,
                        result.Transform?.OffsetY,
                        result.Confidence,
                        result.BestScore,
                        SecondScore = double.IsFinite(result.SecondScore)
                            ? result.SecondScore
                            : (double?)null,
                        result.CandidateMargin,
                        RejectionReason = result.RejectionReason.ToString(),
                        result.FailureReason,
                        SingleGateFallbackReason = singleGateFallbackReason,
                        TopCandidates = result.Candidates,
                        Query = new
                        {
                            result.LockedScale,
                            ReferenceSize = new
                            {
                                Width = result.ReferenceWidth,
                                Height = result.ReferenceHeight
                            },
                            EdgePixels = result.QueryEdgePixels,
                            Bounds = new
                            {
                                X = result.QueryBoundsX,
                                Y = result.QueryBoundsY,
                                Width = result.QueryBoundsWidth,
                                Height = result.QueryBoundsHeight
                            },
                            result.ScaleHypothesisCount,
                            result.OversizedHypothesisCount,
                            result.UsedRestrictedSearch,
                            result.WasForcedBestCandidate
                        },
                        Timings = new
                        {
                            result.PreprocessMilliseconds,
                            result.SearchMilliseconds,
                            result.RefineMilliseconds
                        }
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must not change the acceptance decision.
        }
    }

    private static bool TryValidateRanking(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapRecognitionTuning tuning,
        MapScanDiagnostics diagnostics,
        out MapRecognitionAttempt? failure)
    {
        if (ranked.Count == 0)
        {
            failure = Failure(diagnostics, "没有可参与双门几何排名的地图。");
            return false;
        }
        if (ranked[0].VectorError > tuning.VectorErrorTolerance)
        {
            failure = GeometryFailure(
                diagnostics,
                ranked[0].VectorError,
                tuning.VectorErrorTolerance);
            return false;
        }
        failure = null;
        return true;
    }

    private static MapRecognitionAttempt GeometryFailure(
        MapScanDiagnostics diagnostics,
        double error,
        double tolerance) =>
        Failure(
            diagnostics,
            $"地图区域或双门坐标不一致，请重新校准（误差 {error:F3}，阈值 {tolerance:F3}）。");

    private static MapRecognitionAttempt Failure(
        MapScanDiagnostics diagnostics,
        string reason) =>
        new()
        {
            Diagnostics = diagnostics,
            FailureReason = reason
        };

    private IReadOnlyList<MapRecognitionChoice> BuildChoices(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        MapRecognitionSource source,
        int maxCount = 9)
    {
        var choices = new List<MapRecognitionChoice>();
        foreach (var candidate in ranked.Take(maxCount))
        {
            if (candidate.VectorError > tuning.VectorErrorTolerance)
                continue;
            if (!TryBuildRecognition(
                    candidate,
                    alignmentMode,
                    tuning,
                    margin,
                    usedConfirmation: false,
                    source,
                    wasForcedBestResult: false,
                    out var recognition,
                    out _))
            {
                continue;
            }
            choices.Add(new MapRecognitionChoice
            {
                Recognition = recognition!,
                VectorError = candidate.VectorError
            });
        }
        return choices;
    }

    private MapRecognitionAttempt FailureWithChoices(
        MapScanDiagnostics diagnostics,
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        MapRecognitionSource source,
        string reason,
        int maxCandidates = 9)
    {
        var choices = BuildChoices(ranked, alignmentMode, tuning, margin, source, maxCandidates);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Choices = choices,
            FailureReason = choices.Count > 0
                ? reason + " 请从候选中选择，或取消后重试。"
                : reason
        };
    }

    private MapScanDiagnostics CreateDiagnostics() => new()
    {
        ReadyMapCount = ReadyMapCount,
        TotalMapCount = TotalMapCount
    };

    private static double GeometryMargin(IReadOnlyList<MapGeometryCandidate> ranked) =>
        ranked.Count > 1
            ? ranked[1].VectorError - ranked[0].VectorError
            : double.PositiveInfinity;

    private static MapRecognitionTuning NormalizedCopy(MapRecognitionTuning tuning)
    {
        var copy = tuning?.Clone() ?? new MapRecognitionTuning();
        copy.Normalize();
        return copy;
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
        var mainRefBounds = ToPixelBounds(
            main.Bounds,
            profile.RecognitionPixelWidth,
            profile.RecognitionPixelHeight);
        var sideRefBounds = ToPixelBounds(
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
            MainPoint = Center(main.Bounds),
            SidePoint = Center(side.Bounds),
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

    private static double ConfirmCandidate(
        MapGeometryCandidate candidate,
        Mat liveEdges,
        MapScreenRect viewportBounds)
    {
        using var reference = Cv2.ImRead(
            candidate.Fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
        if (reference.Empty())
            return 0d;
        using var referenceEdges = GateTemplateDetector.CreateEdges(reference);
        var fingerprint = candidate.Fingerprint;
        var referenceMain = new Point2d(
            fingerprint.MainPoint.X * referenceEdges.Width,
            fingerprint.MainPoint.Y * referenceEdges.Height);
        var referenceSide = new Point2d(
            fingerprint.SidePoint.X * referenceEdges.Width,
            fingerprint.SidePoint.Y * referenceEdges.Height);
        var liveMain = new Point2d(
            candidate.MainGate.ScreenBounds.CenterX - viewportBounds.X,
            candidate.MainGate.ScreenBounds.CenterY - viewportBounds.Y);
        var liveSide = new Point2d(
            candidate.SideGate.ScreenBounds.CenterX - viewportBounds.X,
            candidate.SideGate.ScreenBounds.CenterY - viewportBounds.Y);
        var referenceDistance = Distance(referenceMain, referenceSide);
        var liveDistance = Distance(liveMain, liveSide);
        if (referenceDistance <= 1d || liveDistance <= 1d)
            return 0d;
        var scale = liveDistance / referenceDistance;
        var patchSize = (int)Math.Clamp(
            ((candidate.MainGate.ScreenBounds.Width + candidate.SideGate.ScreenBounds.Width) / 2d) * 3d,
            96d,
            240d);
        var referencePatchSize = Math.Max(16, (int)Math.Round(patchSize / scale));
        var referenceCenter = new Point2d(
            (referenceMain.X + referenceSide.X) / 2d,
            (referenceMain.Y + referenceSide.Y) / 2d);
        var liveCenter = new Point2d(
            (liveMain.X + liveSide.X) / 2d,
            (liveMain.Y + liveSide.Y) / 2d);
        var referenceCenters = new List<Point2d>
        {
            referenceMain,
            referenceSide,
            referenceCenter
        };
        var liveCenters = new List<Point2d>
        {
            liveMain,
            liveSide,
            liveCenter
        };
        var scaleX = AxisScale(
            referenceSide.X - referenceMain.X,
            liveSide.X - liveMain.X,
            scale);
        var scaleY = AxisScale(
            referenceSide.Y - referenceMain.Y,
            liveSide.Y - liveMain.Y,
            scale);
        foreach (var anchor in (MapFloorRules.GetFloorProfile(
                     candidate.Fingerprint.Map,
                     candidate.Fingerprint.FloorKey)
                 ?? candidate.Fingerprint.Map.Recognition.FirstFloor).Anchors
                     .Where(anchor =>
                         anchor.Role == RecognitionAnchorRole.Optional
                         && anchor.Bounds?.IsValid is true)
                     .Take(3))
        {
            var bounds = anchor.Bounds!;
            var anchorReferenceCenter = new Point2d(
                (bounds.X + (bounds.Width / 2d)) * referenceEdges.Width,
                (bounds.Y + (bounds.Height / 2d)) * referenceEdges.Height);
            referenceCenters.Add(anchorReferenceCenter);
            liveCenters.Add(new Point2d(
                liveCenter.X + ((anchorReferenceCenter.X - referenceCenter.X) * scaleX),
                liveCenter.Y + ((anchorReferenceCenter.Y - referenceCenter.Y) * scaleY)));
        }
        var scores = new List<double>();
        for (var index = 0; index < referenceCenters.Count; index++)
        {
            if (!TryExtractCenteredPatch(
                    referenceEdges,
                    referenceCenters[index],
                    referencePatchSize,
                    out var referencePatch)
                || !TryExtractCenteredPatch(
                    liveEdges,
                    liveCenters[index],
                    patchSize,
                    out var livePatch))
            {
                continue;
            }
            using (referencePatch)
            using (livePatch)
            using (var resized = new Mat())
            {
                Cv2.Resize(
                    referencePatch,
                    resized,
                    livePatch.Size(),
                    0d,
                    0d,
                    InterpolationFlags.Area);
                scores.Add(CosineSimilarity(resized, livePatch));
            }
        }
        return scores.Count == 0 ? 0d : scores.Average();
    }

    private static CvAnchorEvidence CreateEvidence(
        RecognitionAnchor anchor,
        GateDetection gate,
        MapGeometryFingerprint fingerprint)
    {
        var bounds = anchor.Bounds!;
        return new CvAnchorEvidence
        {
            AnchorId = anchor.Id,
            Score = gate.Score,
            TemplateScale = gate.Scale,
            ReferenceBounds = new MapScreenRect(
                bounds.X * fingerprint.ReferenceWidth,
                bounds.Y * fingerprint.ReferenceHeight,
                bounds.Width * fingerprint.ReferenceWidth,
                bounds.Height * fingerprint.ReferenceHeight),
            ScreenBounds = gate.ScreenBounds
        };
    }

    private static MapNormalizedPoint Center(NormalizedRectangle bounds) =>
        new(bounds.X + (bounds.Width / 2d), bounds.Y + (bounds.Height / 2d));

    private static MapScreenRect ToPixelBounds(
        NormalizedRectangle bounds,
        int width,
        int height) =>
        new(
            bounds.X * width,
            bounds.Y * height,
            bounds.Width * width,
            bounds.Height * height);

    private static bool TryExtractCenteredPatch(
        Mat image,
        Point2d center,
        int size,
        out Mat patch)
    {
        patch = new Mat();
        var half = size / 2;
        var left = Math.Max(0, (int)Math.Round(center.X) - half);
        var top = Math.Max(0, (int)Math.Round(center.Y) - half);
        var right = Math.Min(image.Width, left + size);
        var bottom = Math.Min(image.Height, top + size);
        left = Math.Max(0, right - size);
        top = Math.Max(0, bottom - size);
        if (right - left < 12 || bottom - top < 12)
            return false;
        patch = new Mat(image, new Rect(left, top, right - left, bottom - top)).Clone();
        return true;
    }

    private static double CosineSimilarity(Mat left, Mat right)
    {
        using var leftFloat = new Mat();
        using var rightFloat = new Mat();
        left.ConvertTo(leftFloat, MatType.CV_32FC1);
        right.ConvertTo(rightFloat, MatType.CV_32FC1);
        var denominator = Cv2.Norm(leftFloat) * Cv2.Norm(rightFloat);
        return denominator <= 0.000001d
            ? 0d
            : Math.Clamp(leftFloat.Dot(rightFloat) / denominator, 0d, 1d);
    }

    private static double Distance(Point2d left, Point2d right)
    {
        var deltaX = right.X - left.X;
        var deltaY = right.Y - left.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static double AxisScale(
        double referenceDelta,
        double liveDelta,
        double fallbackScale)
    {
        if (Math.Abs(referenceDelta) > 4d)
        {
            var solved = liveDelta / referenceDelta;
            if (double.IsFinite(solved) && solved > 0d)
                return solved;
        }
        return fallbackScale;
    }

    // ── 侧门特征缓存与扫描 ────────────────────────────────────────────

    /// <summary>
    /// 构建侧门特征缓存：遍历所有地图，加载有效特征图为灰度 Mat。
    /// </summary>
    private Dictionary<(Guid, string), Mat> BuildSideEntranceFeatureCache(
        IReadOnlyList<MapRecord> maps)
    {
        var cache = new Dictionary<(Guid, string), Mat>();
        foreach (var map in maps)
        {
            foreach (var floorDef in MapFloorRules.GetOrderedFloors(map))
            {
                var profile = MapFloorRules.GetFloorProfile(map, floorDef.Key);
                if (profile is null
                    || string.IsNullOrWhiteSpace(profile.SideEntranceFeatureFileName))
                    continue;

                try
                {
                    var path = _repository.GetSideEntranceFeaturePath(map, floorDef.Key);
                    if (!File.Exists(path))
                        continue;
                    var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
                    if (mat.Empty())
                    {
                        mat.Dispose();
                        continue;
                    }
                    cache[(map.Id, floorDef.Key)] = mat;
                }
                catch
                {
                    // 单张地图加载失败不影响其他地图
                }
            }
        }
        return cache;
    }

    /// <summary>
    /// 使用侧门特征缓存对捕获帧执行模板匹配，返回 top-<paramref name="topK"/> 候选。
    /// </summary>
    public IReadOnlyList<SideEntranceScanCandidate> RunSideEntranceScan(
        Mat capturedFrame,
        int topK = 5)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (capturedFrame.Empty() || _sideEntranceFeatureCache.Count == 0)
            return [];

        var candidates = new List<(MapRecord map, string floorKey, Mat template)>(
            _sideEntranceFeatureCache.Count);

        foreach (var ((mapId, floorKey), template) in _sideEntranceFeatureCache)
        {
            var map = _maps.FirstOrDefault(m => m.Id == mapId);
            if (map is not null)
                candidates.Add((map, floorKey, template));
        }

        return _sideEntrancePipeline.RunScan(capturedFrame, candidates, topK);
    }

    private static string ResolveGatePath()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Assets", "Gate.png");
        if (File.Exists(deployed))
            return deployed;
        var workspace = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Gate.png"));        if (File.Exists(workspace))
            return workspace;
        var current = Path.Combine(Environment.CurrentDirectory, "Assets", "Gate.png");
        return File.Exists(current) ? current : deployed;
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
