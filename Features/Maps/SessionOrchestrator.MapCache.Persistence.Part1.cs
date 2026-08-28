namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

    private async Task FlushAutomaticMapCacheAsync()
    {
        Dictionary<MapFeatureCacheKey, MapScaleSample[]> snapshot;
        Dictionary<MapFeatureCacheKey, MapFeatureCacheEntry> pendingEntries;
        lock (_automaticMapCacheGate)
        {
            snapshot = _automaticMapCacheSamples.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray());
            pendingEntries = new(_pendingAutomaticMapCacheEntries);
        }

        if (_settings?.AllowAutomaticMapCache is not true)
        {
            ResetAutomaticMapCacheSamples();
            return;
        }

        var saved = 0;
        var unstable = 0;
        var failed = 0;
        var skippedManual = 0;
        var entriesToPersist = new Dictionary<MapFeatureCacheKey, MapFeatureCacheEntry>(
            pendingEntries);
        foreach (var (key, samples) in snapshot)
        {
            if (!MapScaleSampleAggregator.TryAggregate(samples, out var aggregate)
                || aggregate is null)
            {
                unstable++;
                continue;
            }
            if (!entriesToPersist.TryGetValue(key, out var staged)
                || staged.Scale.Source is not (
                    MapFeatureCacheSource.Recovery
                    or MapFeatureCacheSource.CrossResolutionValidated))
            {
                entriesToPersist[key] = CreateCacheEntry(
                    key,
                    aggregate.Scale,
                    MapFeatureCacheSource.Automatic,
                    aggregate.SampleCount,
                    aggregate.Confidence,
                    aggregate.RelativeMedianAbsoluteDeviation,
                    _lastAlignmentObservedDpi,
                    candidateMargin: aggregate.CandidateMargin);
            }
        }

        foreach (var (key, entry) in entriesToPersist)
        {
            if (_mapFeatureCacheRepository.TryGet(key, out var existing)
                && !MapFeatureCacheRules.CanReplaceExistingEntry(
                    existing,
                    entry))
            {
                skippedManual++;
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "人工缩放缓存保持生效：修复证据未达到覆盖门槛",
                    details: new()
                    {
                        ["mapId"] = key.MapId,
                        ["floor"] = key.FloorKey,
                        ["candidateSource"] = entry.Scale.Source.ToString(),
                        ["sampleCount"] = entry.Scale.SampleCount,
                        ["localizationConfidence"] =
                            entry.Scale.Validation?
                                .LastLocalizationConfidence,
                        ["candidateMargin"] = entry.Scale.Validation?
                            .LastCandidateMargin,
                        ["cacheDecision"] = "manual-kept"
                    });
                continue;
            }
            try
            {
                await UpsertMapCacheAsync(entry, requireActiveLease: false);
                if (entry.Scale.Source == MapFeatureCacheSource.Recovery)
                    CompleteMapCacheRepair(key);
                saved++;
            }
            catch (Exception ex)
            {
                failed++;
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Error,
                    $"自动地图缓存保存失败 · map={key.MapId} · "
                    + $"floor={key.FloorKey} · {ex.Message}",
                    details: new()
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["stackTrace"] = ex.ToString()
                    });
            }
        }

        lock (_automaticMapCacheGate)
        {
            _automaticMapCacheSamples.Clear();
            _pendingAutomaticMapCacheEntries.Clear();
        }
        _logCollector.Append(
            MapLogCategory.Session,
            failed == 0 ? MapLogLevel.Info : MapLogLevel.Warning,
            $"本局自动地图缓存落盘完成 · saved={saved} · "
            + $"unstable={unstable} · staged={pendingEntries.Count} · "
            + $"skippedManual={skippedManual} · failed={failed} · "
            + $"groups={snapshot.Count}");
    }

    /// <summary>
    /// Fire-and-forget 记录一次缓存验证结果。失败计数（FailedValidationCount）
    /// 是读路径信任降级的证据，必须始终可落盘——不 gate 在
    /// <see cref="MapRuntimeSettings.AllowAutomaticMapCache"/> 上，否则关闭自动
    /// 缓存后毒条目永远不会被降级。
    /// </summary>
    private void NoteCacheValidationOutcome(
        MapFeatureCacheKey key,
        bool succeeded)
    {
        _ = PersistCacheValidationOutcomeAsync(key, succeeded);
    }

    private async Task PersistCacheValidationOutcomeAsync(
        MapFeatureCacheKey key,
        bool succeeded)
    {
        try
        {
            if (!_mapFeatureCacheRepository.TryGet(key, out var existing)
                || existing is null)
            {
                return;
            }
            var outcome = MapFeatureCacheRules.RecordValidationOutcome(
                existing.Scale.Validation,
                succeeded,
                DateTimeOffset.UtcNow);
            if (outcome is null)
                return; // 成功且无失败历史：快乐路径零写

            var updated = CopyEntryWithValidation(existing, outcome);
            await UpsertMapCacheAsync(updated);
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"缩放缓存验证结果已记录 · map={key.MapId} · "
                + $"floor={key.FloorKey} · succeeded={succeeded}",
                details: new()
                {
                    ["mapId"] = key.MapId,
                    ["floor"] = key.FloorKey,
                    ["succeeded"] = succeeded,
                    ["failedValidationCount"] = outcome.FailedValidationCount,
                    ["successfulValidationCount"] =
                        outcome.SuccessfulValidationCount,
                    ["distrusted"] = !MapFeatureCacheRules.IsCacheEntryTrusted(
                        updated)
                });
        }
        catch (Exception ex)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                $"缩放缓存验证结果落盘失败 · map={key.MapId} · "
                + $"floor={key.FloorKey}",
                details: new()
                {
                    ["succeeded"] = succeeded,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["exception"] = ex.ToString()
                });
        }
    }

}
