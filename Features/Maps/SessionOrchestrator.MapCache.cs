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

    private void MarkMapCacheForRepair(MapFeatureCacheKey key)
    {
        lock (_automaticMapCacheGate)
            _mapCacheRepairPendingKeys.Add(key);
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
            return fallback();
        }

        var key = MapFeatureCacheRules.CreateKey(map, floorKey, resolution);
        if (!_mapFeatureCacheRepository.TryGet(key, out var entry)
            || entry is null)
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
            return fallback();
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
            return fallback();
        }
        var cachedAlignmentTimer =
            System.Diagnostics.Stopwatch.StartNew();
        var cachedAttempt = _recognition.AlignWithCachedScale(
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
        cachedAlignmentTimer.Stop();
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            cachedAttempt.Recognition is null
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
                ["cacheDecision"] = cachedAttempt.Recognition is null
                    ? "rejected-recovery-required"
                    : "accepted",
                ["failureReason"] = cachedAttempt.FailureReason
            });
        if (cachedAttempt.Recognition is { } cachedRecognition)
        {
            return CopyAttempt(
                cachedAttempt,
                MarkUsedCachedScale(cachedRecognition));
        }

        repairKey = key;
        return fallback();
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
        lock (_automaticMapCacheGate)
        {
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
