namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private Task RepairMapCacheAsync(
        MapFeatureCacheKey? key,
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        if (_settings?.AllowAutomaticMapCache is not true
            || key is null
            || recognition.Result.OverlayTransform is not { } transform
            || recognition.Result.ReusedLastTransform
            || !MapFeatureCacheRules.IsReliableLocalizationSample(
                recognition.Result,
                _settings.SessionTuning.HighConfidence,
                _settings.StructureRegistrationTuning.MinimumCandidateMargin)
            || !TryGetUniformScale(transform, out var scale))
        {
            if (key is not null)
            {
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Warning,
                    "缩放缓存修复样本已拒绝",
                    details: new()
                    {
                        ["mapId"] = key.MapId,
                        ["floor"] = key.FloorKey,
                        ["identityConfidence"] =
                            recognition.Result.IdentityConfidence,
                        ["localizationConfidence"] =
                            recognition.Result.LocalizationConfidence,
                        ["candidateMargin"] =
                            MapFeatureCacheRules.GetCandidateMargin(
                                recognition.Result),
                        ["repairReason"] = "weak-localization-evidence"
                    });
            }
            return Task.CompletedTask;
        }

        MapCacheRepairAggregate? aggregate;
        lock (_automaticMapCacheGate)
        {
            if (!_mapCacheRepairSamples.TryGetValue(key, out var samples))
            {
                samples = [];
                _mapCacheRepairSamples[key] = samples;
            }
            samples.Add(new MapCacheRepairSample(
                scale,
                transform.OffsetX,
                transform.OffsetY,
                recognition.Result.LocalizationConfidence,
                MapFeatureCacheRules.GetCandidateMargin(recognition.Result)));
            while (samples.Count
                > MapCacheRepairSampleAggregator.RequiredConsecutiveSamples)
            {
                samples.RemoveAt(0);
            }
            MapCacheRepairSampleAggregator.TryAggregate(samples, out aggregate);
        }

        if (aggregate is null)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "缩放缓存修复证据尚未满足三次连续一致性",
                details: new()
                {
                    ["mapId"] = key.MapId,
                    ["floor"] = key.FloorKey,
                    ["identityConfidence"] =
                        recognition.Result.IdentityConfidence,
                    ["localizationConfidence"] =
                        recognition.Result.LocalizationConfidence,
                    ["candidateMargin"] =
                        MapFeatureCacheRules.GetCandidateMargin(
                            recognition.Result),
                    ["repairReason"] = "awaiting-consistent-samples"
                });
            return Task.CompletedTask;
        }

        // 语义修正 C：全新验证元数据，失败计数清零——既不把"修复样本数"
        // 误当失败次数，也不继承毒缓存的失败历史（否则新 Recovery 条目
        // 会立即被信任门槛降级）。
        var validation = MapFeatureCacheRules.CreateRepairValidation(aggregate);
        StageAutomaticMapCacheEntry(CreateCacheEntry(
            key,
            aggregate.Scale,
            MapFeatureCacheSource.Recovery,
            aggregate.SampleCount,
            aggregate.LocalizationConfidence,
            aggregate.RelativeMedianAbsoluteDeviation,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
            validation: validation,
            candidateMargin: aggregate.CandidateMargin));
        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "缩放缓存修复已获得三次连续一致证据，等待落盘",
            details: new()
            {
                ["mapId"] = key.MapId,
                ["floor"] = key.FloorKey,
                ["scale"] = aggregate.Scale,
                ["localizationConfidence"] = aggregate.LocalizationConfidence,
                ["candidateMargin"] = aggregate.CandidateMargin,
                ["repairReason"] = "consistent-recovery-samples"
            });
        return Task.CompletedTask;
    }

    private Task PersistPreprocessedScaleAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame,
        MapScanDiagnostics? diagnostics)
    {
        if (_settings?.AllowAutomaticMapCache is not true
            || diagnostics is not
                {
                    ScaleBootstrapSucceeded: true,
                    ScaleBootstrapValidated: true,
                    StructureAccepted: true
                }
            || diagnostics.ScaleBootstrapUniqueMatches
                < MapVpsgScaleEstimator.MinimumUniqueMatches
            || diagnostics.ScaleBootstrapPairVotes
                < MapVpsgScaleEstimator.MinimumPairVotes
            || diagnostics.ScaleBootstrapConfidence
                < _settings.SessionTuning.HighConfidence
            || recognition.Result.OverlayTransform is not { } transform
            || recognition.Result.ReusedLastTransform
            || !MapFeatureCacheRules.IsReliableLocalizationSample(
                recognition.Result,
                _settings.SessionTuning.HighConfidence,
                _settings.StructureRegistrationTuning.MinimumCandidateMargin)
            || !string.Equals(
                _currentFloorKey ?? recognition.Result.Floor,
                recognition.Result.Floor,
                StringComparison.Ordinal)
            || !TryGetUniformScale(transform, out var scale))
        {
            return Task.CompletedTask;
        }

        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return Task.CompletedTask;
        var key = MapFeatureCacheRules.CreateKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        if (_mapFeatureCacheRepository.TryGet(key, out var existing)
            && existing is not null
            && (existing.Scale.Source == MapFeatureCacheSource.Manual
                || existing.Scale.Confidence
                    > diagnostics.ScaleBootstrapConfidence))
        {
            return Task.CompletedTask;
        }

        StageAutomaticMapCacheEntry(CreateCacheEntry(
            key,
            scale,
            MapFeatureCacheSource.PreprocessedEstimate,
            1,
            diagnostics.ScaleBootstrapConfidence,
            diagnostics.ScaleBootstrapRelativeMad,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
            new MapScaleEstimationEvidence
            {
                UniqueMatches = diagnostics.ScaleBootstrapUniqueMatches,
                PairVotes = diagnostics.ScaleBootstrapPairVotes,
                ResidualPixels = diagnostics.ScaleBootstrapResidualPixels,
                RelativeMedianAbsoluteDeviation =
                    diagnostics.ScaleBootstrapRelativeMad
            },
            validation: new MapScaleCacheValidationMetadata
            {
                SuccessfulValidationCount = 1,
                LastLocalizationConfidence =
                    recognition.Result.LocalizationConfidence,
                LastCandidateMargin =
                    MapFeatureCacheRules.GetCandidateMargin(recognition.Result),
                LastValidatedAt = DateTimeOffset.UtcNow
            },
            candidateMargin:
                MapFeatureCacheRules.GetCandidateMargin(recognition.Result)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Persists the player-confirmed transform as the highest-trust cache
    /// source, replacing any existing entry for the same key. Only runs when
    /// the current resolution is cache-supported.
    /// </summary>
    private async Task PersistPlayerDecidedScaleAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        if (IsMatchEnding || !_matchSession.Snapshot.IsStarted)
            return;
        if (recognition.Result.OverlayTransform is not { } transform
            || !TryGetUniformScale(transform, out var scale))
        {
            return;
        }

        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                "玩家缩放未写入缓存：当前捕获分辨率不受支持",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor,
                    ["clientWidth"] = resolution.ClientWidth,
                    ["clientHeight"] = resolution.ClientHeight,
                    ["viewportWidth"] = resolution.ViewportWidth,
                    ["viewportHeight"] = resolution.ViewportHeight
                });
            return;
        }

        var key = MapFeatureCacheRules.CreateKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        try
        {
            await UpsertMapCacheAsync(CreateCacheEntry(
                key,
                scale,
                MapFeatureCacheSource.Player,
                sampleCount: 1,
                confidence: 1d,
                relativeMad: 0d,
                DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
                validation: new MapScaleCacheValidationMetadata
                {
                    DirectlyTrusted = true,
                    SuccessfulValidationCount = 0,
                    LastLocalizationConfidence = 1d,
                    LastCandidateMargin = 1d,
                    LastValidatedAt = default
                },
                candidateMargin: 1d));
            CompleteMapCacheRepair(key);
            lock (_automaticMapCacheGate)
            {
                _automaticMapCacheSamples.Remove(key);
                _pendingAutomaticMapCacheEntries.Remove(key);
            }
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"玩家缩放已写入缓存 · map={key.MapId} · "
                + $"floor={key.FloorKey} · scale={scale:F6}");
        }
        catch (Exception ex)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Error,
                $"玩家缩放缓存写入失败 · map={key.MapId} · "
                + $"floor={key.FloorKey} · {ex.Message}",
                details: new()
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["stackTrace"] = ex.ToString()
                });
        }
    }

    private async Task SaveCurrentMapCacheAsync()
    {
        if (_matchSession.Snapshot.Mode == MapRunMode.Survey)
        {
            await CaptureSurveyFrameOnDemandAsync();
            return;
        }
        await _matchLifecycleGate.WaitAsync();
        try
        {
            await SaveCurrentMapCacheCoreAsync();
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }

    private async Task SaveCurrentMapCacheCoreAsync()
    {
        if (IsMatchEnding || !_matchSession.Snapshot.IsStarted)
        {
            // The binding is global, but a cache save is match-scoped. Ignore
            // late game/UI key events after exit instead of repeatedly
            // recreating an error status overlay over a finished match.
            return;
        }

        var operationMatch = _matchSession.Snapshot;
        string? failure = null;
        if (_settings is null)
            failure = "地图运行时尚未初始化。";
        else if (!_hasCompletedQuickScanAlignment)
            failure = "请先完成一次快捷扫描并锁定地图。";
        else if (_lastRecognition is not { } recognition
            || recognition.Result.OverlayTransform is not { } transform)
            failure = "请先扫描锁定地图并完成一次对齐。";
        else if (!string.Equals(
            _currentFloorKey ?? recognition.Result.Floor,
            recognition.Result.Floor,
            StringComparison.Ordinal))
            failure = "当前楼层尚未完成对齐。";
        else if (!TryGetUniformScale(transform, out var scale))
            failure = "本次对齐没有可保存的统一缩放值。";
        else if (!TryLockCurrentAdaptiveScale(recognition, scale))
            failure = "当前临时对齐已失效，请重新打开地图并完成对齐。";
        else
        {
            RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
            RememberReliableFloorAlignment(
                operationMatch,
                recognition,
                _lastAlignmentSession);
            if (!IsCurrentMatchOperation(operationMatch))
                return;
            _statusMessage = "当前楼层缩放已在本局锁定。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"玩家已锁定本局缩放 · map={recognition.Map.Id} · "
                + $"floor={recognition.Result.Floor} · scale={scale:F6}");
            ShowCacheBindingStatus(
                MapOverlayStatusLevel.Success,
                "本局缩放已锁定",
                $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()} · {scale:F6}");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!IsCurrentMatchOperation(operationMatch))
            return;
        _statusMessage = $"本局缩放锁定失败：{failure}";
        ShowCacheBindingStatus(
            MapOverlayStatusLevel.Failure,
            "本局缩放锁定失败",
            failure!);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowCacheBindingStatus(
        MapOverlayStatusLevel level,
        string title,
        string message)
    {
        if (!_lastGameBounds.IsValid || _lastGameWindowHandle == IntPtr.Zero)
            return;
        ShowTransientOverlayStatus(
            level,
            title,
            message,
            string.Empty,
            _lastGameBounds,
            _lastGameWindowHandle);
    }

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
                await UpsertMapCacheAsync(entry);
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
