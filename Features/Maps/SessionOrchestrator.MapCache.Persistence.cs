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
            || !IsCacheKeyForCurrentLease(key)
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
            if (!IsCacheKeyForCurrentLease(key))
                return Task.CompletedTask;
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

    private async Task PersistPreprocessedScaleAsync(
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
            || (!string.Equals(
                    diagnostics.ScaleBootstrapMethod,
                    "structure",
                    StringComparison.Ordinal)
                && diagnostics.ScaleBootstrapUniqueMatches
                    < MapVpsgScaleEstimator.MinimumUniqueMatches)
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
            return;
        }

        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return;
        var key = CreateAlignmentCacheKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        if (_mapFeatureCacheRepository.TryGet(key, out var existing)
            && existing is not null
            && (existing.Scale.Source == MapFeatureCacheSource.Manual
                || existing.Scale.Confidence
                    > diagnostics.ScaleBootstrapConfidence))
        {
            return;
        }

        var entry = CreateCacheEntry(
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
                MapFeatureCacheRules.GetCandidateMargin(recognition.Result));
        // 立即落盘：VPSG 成功即有高置信缩放证据，本局内后续开图即可命中缓存
        // 走 fixed 验证，避免本局反复重算 VPSG + 全局恢复（此前仅 Stage 到
        // 内存 pending，查询走磁盘 repository，导致本局内从未命中本局产生的
        // 缓存，命中率仅来自上一局落盘的条目）。保留 pending 暂存，使结束
        // 对局时样本聚合有机会升级为更可信的 Automatic 条目。
        StageAutomaticMapCacheEntry(entry);
        try
        {
            await UpsertMapCacheAsync(entry);
        }
        catch (Exception ex)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                $"VPSG 缩放缓存即时落盘失败 · map={key.MapId} · "
                + $"floor={key.FloorKey} · {ex.Message}",
                details: new()
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["stackTrace"] = ex.ToString()
                });
        }
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

        var key = CreateAlignmentCacheKey(
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
        if (!CanSaveCurrentMapCache())
            return;
        if (_matchSession.Snapshot.Mode == MapRunMode.Survey)
        {
            await CaptureSurveyFrameOnDemandAsync();
            return;
        }
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (!CanSaveCurrentMapCache())
                return;
            await SaveCurrentMapCacheCoreAsync();
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }

    private async Task SaveCurrentMapCacheCoreAsync()
    {
        if (IsMatchEnding
            || !_matchSession.Snapshot.IsStarted
            || !CanSaveCurrentMapCache())
        {
            // The binding is global, but a cache save is match-scoped. Ignore
            // late game/UI key events after exit or while the large map is not
            // open or no map identity is locked.
            return;
        }

        var operationMatch = _matchSession.Snapshot;
        var openSession = _mapOpenSession.Snapshot;
        string? failure = null;
        if (_settings is null)
            failure = "地图运行时尚未初始化。";
        else if (_lastRecognition is not { } recognition
            || recognition.Result.OverlayTransform is not { } transform)
            failure = "已锁定地图尚无可用的临时缩放，请先完成一次对齐。";
        else if (openSession.MapId != recognition.Map.Id
            || !string.Equals(
                openSession.Floor,
                recognition.Result.Floor,
                StringComparison.Ordinal))
            failure = "当前临时缩放不属于已锁定的地图楼层，请先完成本楼层对齐。";
        else if (!string.Equals(
            _currentFloorKey ?? recognition.Result.Floor,
            recognition.Result.Floor,
            StringComparison.Ordinal))
            failure = "当前楼层尚未完成对齐。";
        else if (!TryGetUniformScale(transform, out var scale))
            failure = "本次对齐没有可保存的统一缩放值。";
        else if (!RememberManualFloorScaleLock(recognition, scale))
            failure = "当前对齐缺少捕获几何，请重新打开地图并完成对齐。";
        else
        {
            // Adaptive state mirrors the player lock when available, but it is
            // not an authorization or persistence prerequisite. The
            // authoritative match-scoped lock above remains usable when
            // adaptive scale is disabled or its open controller was just
            // reconstructed.
            var adaptiveLockUpdated =
                TryLockCurrentAdaptiveScale(recognition, scale);
            RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
            MarkReliableFloorScale(recognition, scale);
            if (!IsCurrentMatchOperation(operationMatch))
                return;
            _statusMessage = "当前楼层缩放已在本局锁定。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"玩家已锁定本局缩放 · map={recognition.Map.Id} · "
                + $"floor={recognition.Result.Floor} · scale={scale:F6}",
                details: new()
                {
                    ["adaptiveLockUpdated"] = adaptiveLockUpdated,
                    ["lockScope"] = "match-map-floor-capture-geometry"
                });
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

    private bool CanSaveCurrentMapCache() =>
        MapOpenAlignmentRouteRules.CanSaveMapCache(
            _gameMapToggleState.IsOpen,
            _mapOpenSession.Snapshot.IsIdentityLocked,
            _matchSession.Snapshot.Mode == MapRunMode.Survey);

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

}
/*
 * 文件职责：SessionOrchestrator.MapCache.Persistence。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
