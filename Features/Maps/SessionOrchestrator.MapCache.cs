using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly object _automaticMapCacheGate = new();
    private readonly SemaphoreSlim _mapCacheWriteGate = new(1, 1);
    private readonly Dictionary<MapFeatureCacheKey, List<MapScaleSample>>
        _automaticMapCacheSamples = [];
    private readonly Dictionary<MapFeatureCacheKey, List<MapCacheRepairSample>>
        _mapCacheRepairSamples = [];
    private readonly HashSet<MapFeatureCacheKey> _mapCacheRepairPendingKeys = [];
    private readonly Dictionary<MapFeatureCacheKey, MapFeatureCacheEntry>
        _pendingAutomaticMapCacheEntries = [];
    private MapCacheResolutionSignature? _lastAlignmentResolution;
    private uint _lastAlignmentObservedDpi;
    private bool _hasCompletedQuickScanAlignment;

    private MapCacheResolutionSignature GetResolution(CapturedGameFrame frame) =>
        MapCacheResolutionSignature.FromBounds(
            frame.ClientBounds,
            frame.ViewportBounds,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle));

    private bool TryGetNoDoorScaleCache(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        out MapFeatureCacheKey? key,
        out MapFeatureCacheEntry? entry)
    {
        key = null;
        entry = null;
        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return false;

        key = MapFeatureCacheRules.CreateKey(map, floorKey, resolution);
        if (!_mapFeatureCacheRepository.TryGet(key, out entry)
            || entry is null)
        {
            entry = null;
            return false;
        }

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"无门路线缩放缓存命中 · floor={floorKey} · scale={entry.Scale.UniformScale:F6}",
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["scale"] = entry.Scale.UniformScale,
                ["source"] = entry.Scale.Source.ToString(),
                ["directlyTrusted"] =
                    entry.Scale.Validation?.DirectlyTrusted ?? false,
                ["sampleCount"] = entry.Scale.SampleCount,
                ["cacheDecision"] = "trusted-seed"
            });
        return true;
    }

    /// <summary>
    /// 本帧是否已有可信的缩放种子（自适应标定 / 未降级的落盘缓存）。纯查询，
    /// 不写日志、不改状态，供路线选择在跑昂贵的证据采集之前短路使用。
    /// </summary>
    private bool HasTrustedScaleSeed(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey)
    {
        if (TryGetAdaptiveScaleSeed(frame, map, floorKey, out var adaptiveSeed)
            && adaptiveSeed is not null)
        {
            return true;
        }

        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return false;
        var key = MapFeatureCacheRules.CreateKey(map, floorKey, resolution);
        return _mapFeatureCacheRepository.TryGet(key, out var entry)
            && entry is not null
            && MapFeatureCacheRules.IsCacheEntryTrusted(entry);
    }

    private void MarkMapCacheForRepair(MapFeatureCacheKey key)
    {
        if (!IsCacheKeyForCurrentLease(key))
            return;
        lock (_automaticMapCacheGate)
        {
            if (IsCacheKeyForCurrentLease(key))
                _mapCacheRepairPendingKeys.Add(key);
        }
    }

    private bool TryGetPendingMapCacheRepairKey(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        out MapFeatureCacheKey? key)
    {
        key = null;
        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return false;
        var candidate = MapFeatureCacheRules.CreateKey(
            map,
            floorKey,
            resolution);
        lock (_automaticMapCacheGate)
        {
            if (!_mapCacheRepairPendingKeys.Contains(candidate))
                return false;
        }
        key = candidate;
        return true;
    }

    private void CompleteMapCacheRepair(MapFeatureCacheKey key)
    {
        lock (_automaticMapCacheGate)
        {
            _mapCacheRepairPendingKeys.Remove(key);
            _mapCacheRepairSamples.Remove(key);
        }
    }

    private MapRecognitionAttempt AlignUsingScaleCache(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        Func<MapRecognitionAttempt> fallback,
        out MapFeatureCacheKey? repairKey)
    {
        repairKey = null;
        using var cacheRoute = MapOperationTraceAmbient.StartChild(
            "scale_cache_route",
            MapOperationWaitKind.Io,
            mapId: map.Id.ToString("D"),
            floorKey: floorKey);
        MapRecognitionAttempt RunScaleFallback(string reason)
        {
            using var fallbackSpan = MapOperationTraceAmbient.StartChild(
                "scale_cache_fallback",
                MapOperationWaitKind.Compute,
                mapId: map.Id.ToString("D"),
                floorKey: floorKey);
            try
            {
                return fallback();
            }
            finally
            {
                fallbackSpan.Complete(
                    MapOperationSpanStatus.Completed,
                    reason);
            }
        }
        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "缩放缓存未使用：当前捕获分辨率不受支持",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["clientWidth"] = resolution.ClientWidth,
                    ["clientHeight"] = resolution.ClientHeight,
                    ["viewportWidth"] = resolution.ViewportWidth,
                    ["viewportHeight"] = resolution.ViewportHeight
                });
            return RunScaleFallback("unsupported-resolution");
        }

        var key = MapFeatureCacheRules.CreateKey(map, floorKey, resolution);
        bool adaptiveSeedAttempted;
        MapRecognitionAttempt? adaptiveAttempt;
        using (var bootstrapSpan = MapOperationTraceAmbient.StartChild(
                   "vpsg_scale_bootstrap",
                   MapOperationWaitKind.Compute,
                   mapId: map.Id.ToString("D"),
                   floorKey: floorKey))
        {
            adaptiveSeedAttempted = TryAlignWithAdaptiveCalibrationSeed(
                frame,
                map,
                floorKey,
                _settings!.OverlayAlignmentMode,
                tuning,
                structureTuning,
                identityPriorConfidence,
                out _,
                out adaptiveAttempt);
        }
        if (adaptiveSeedAttempted)
        {
            if (IsAdaptiveInitialScaleQualified(adaptiveAttempt, structureTuning)
                && adaptiveAttempt!.Recognition is { } adaptiveRecognition)
            {
                return CopyAttempt(adaptiveAttempt, adaptiveRecognition);
            }
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                "adaptive calibration seed rejected by initial quality gate",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["localizationConfidence"] = adaptiveAttempt?.Recognition?
                        .Result.LocalizationConfidence,
                    ["candidateMargin"] = adaptiveAttempt?.Recognition?
                        .Result.StructureCandidateMargin,
                    ["requiredCandidateMargin"] = structureTuning.MinimumCandidateMargin
                });
            repairKey = key;
            return RunScaleFallback("adaptive-seed-rejected");
        }
        MapFeatureCacheEntry? entry;
        bool cacheHit;
        using (var cacheRead = MapOperationTraceAmbient.StartChild(
                   "scale_cache_read",
                   MapOperationWaitKind.Io,
                   mapId: map.Id.ToString("D"),
                   floorKey: floorKey))
        {
            cacheHit = _mapFeatureCacheRepository.TryGet(key, out entry)
                && entry is not null;
        }
        if (!cacheHit || entry is null)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "缩放缓存未命中，进入常规对齐路线",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["clientWidth"] = resolution.ClientWidth,
                    ["clientHeight"] = resolution.ClientHeight,
                    ["viewportWidth"] = resolution.ViewportWidth,
                    ["viewportHeight"] = resolution.ViewportHeight
                });
            return RunScaleFallback("cache-miss");
        }

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"缩放缓存命中 · floor={floorKey} · scale={entry.Scale.UniformScale:F6}",
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["scale"] = entry.Scale.UniformScale,
                ["source"] = entry.Scale.Source.ToString(),
                ["sampleCount"] = entry.Scale.SampleCount,
                ["confidence"] = entry.Scale.Confidence
            });
        // 信任门槛：连续验证失败达到阈值的条目跳过 fixed 验证，直接走常规
        // 路线。repairKey 保留，使修复样本继续积累，最终由 Recovery 替换毒缓存。
        if (!MapFeatureCacheRules.IsCacheEntryTrusted(entry))
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                $"缩放缓存已降级跳过 · floor={floorKey} · "
                + $"scale={entry.Scale.UniformScale:F6}",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["scale"] = entry.Scale.UniformScale,
                    ["source"] = entry.Scale.Source.ToString(),
                    ["failedValidationCount"] =
                        entry.Scale.Validation?.FailedValidationCount,
                    ["cacheDecision"] = "distrusted-skipped"
                });
            repairKey = key;
            return RunScaleFallback("cache-untrusted");
        }
        var cachedAlignmentTimer =
            System.Diagnostics.Stopwatch.StartNew();
        MapRecognitionAttempt cachedAttempt;
        using (var cachedValidation = MapOperationTraceAmbient.StartChild(
                   "cached_scale_validation",
                   MapOperationWaitKind.Compute,
                   mapId: map.Id.ToString("D"),
                   floorKey: floorKey))
        {
            cachedAttempt = _recognition.AlignWithCachedScale(
                frame,
                map.Id,
                floorKey,
                MapFeatureCacheRules.CreateScaleSeed(
                    map,
                    floorKey,
                    entry.Scale.UniformScale),
                _settings!.OverlayAlignmentMode,
                tuning,
                structureTuning,
                identityPriorConfidence);
        }
        cachedAlignmentTimer.Stop();
        // 已锁定地图的缓存命中验证用宽松门槛（RecoveryConfidence）：缓存条目
        // 本身即高置信证据（VPSG accepted / 历史命中），fixed 验证到 0.80~0.82
        // 即可采纳，避免边缘置信命中被拒后 fallback 回 VPSG 全局恢复（P1-2）。
        var adaptiveQualified = IsAdaptiveInitialScaleUsable(
            cachedAttempt,
            structureTuning);
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            !adaptiveQualified
                ? MapLogLevel.Warning
                : MapLogLevel.Info,
            $"缩放缓存结构验证完成 · success={cachedAttempt.Recognition is not null}",
            elapsedMs: cachedAlignmentTimer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["scale"] = entry.Scale.UniformScale,
                ["liveStructureExtractionMs"] =
                    cachedAttempt.Diagnostics.StructurePreprocessMilliseconds,
                ["structureSearchMs"] =
                    cachedAttempt.Diagnostics.StructureSearchMilliseconds,
                ["structureRefineMs"] =
                    cachedAttempt.Diagnostics.StructureRefineMilliseconds,
                ["rejection"] = cachedAttempt.StructureResult?
                    .RejectionReason.ToString(),
                ["identityConfidence"] = cachedAttempt.Recognition?
                    .Result.IdentityConfidence,
                ["localizationConfidence"] = cachedAttempt.Recognition?
                    .Result.LocalizationConfidence,
                ["candidateMargin"] = cachedAttempt.Recognition is { } cached
                    ? MapFeatureCacheRules.GetCandidateMargin(cached.Result)
                    : cachedAttempt.StructureResult?.CandidateMargin,
                ["cacheDecision"] = adaptiveQualified
                    ? "accepted"
                    : cachedAttempt.Recognition is null
                        ? "rejected-recovery-required"
                        : "rejected-adaptive-quality",
                ["adaptiveQualified"] = adaptiveQualified,
                ["failureReason"] = cachedAttempt.FailureReason
            });
        if (adaptiveQualified
            && cachedAttempt.Recognition is { } cachedRecognition)
        {
            return CopyAttempt(
                cachedAttempt,
                MarkUsedCachedScale(cachedRecognition));
        }

        repairKey = key;
        return RunScaleFallback("cached-validation-rejected");
    }

    private void RecordSuccessfulAlignment(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        if (IsMatchEnding || !_matchSession.Snapshot.IsStarted)
            return;
        var transform = recognition.Result.OverlayTransform;
        var resolution = GetResolution(frame);
        _lastAlignmentResolution = resolution.IsSupported ? resolution : null;
        _lastAlignmentObservedDpi =
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle);
        if (_settings?.AllowAutomaticMapCache is not true
            || !_hasCompletedQuickScanAlignment
            || !resolution.IsSupported
            || transform is null
            || recognition.Result.ReusedLastTransform
            || recognition.Result.UsedCachedScale
            || !MapFeatureCacheRules.IsReliableLocalizationSample(
                recognition.Result,
                _settings.SessionTuning.HighConfidence,
                _settings.StructureRegistrationTuning.MinimumCandidateMargin)
            || !TryGetUniformScale(transform, out var scale))
        {
            return;
        }

        var key = MapFeatureCacheRules.CreateKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        if (!IsCacheKeyForCurrentLease(key))
            return;
        lock (_automaticMapCacheGate)
        {
            if (!IsCacheKeyForCurrentLease(key))
                return;
            if (!_automaticMapCacheSamples.TryGetValue(key, out var samples))
            {
                samples = [];
                _automaticMapCacheSamples[key] = samples;
            }
            samples.Add(new MapScaleSample(
                scale,
                recognition.Result.LocalizationConfidence,
                MapFeatureCacheRules.GetCandidateMargin(recognition.Result)));
        }
    }

}
/*
 * 文件职责：SessionOrchestrator.MapCache。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
