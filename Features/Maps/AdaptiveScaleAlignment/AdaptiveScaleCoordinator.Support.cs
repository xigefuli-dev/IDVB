namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed partial class AdaptiveScaleCoordinator
{
    private void AddInitialObservations(
        AdaptiveScaleController controller,
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform,
        AdaptiveScaleInitialEvidence evidence)
    {
        if (IsStrongStructure(recognition, evidence))
        {
            controller.ObserveAbsolute(new AdaptiveScaleObservation(
                evidence.FrameId,
                DateTimeOffset.UtcNow,
                UniformScale(transform),
                recognition.Result.LocalizationConfidence,
                recognition.Result.StructureCandidateMargin,
                AdaptiveScaleObservationSource.Structure,
                transform));
        }
        if (!IsStrongVpsg(evidence.Vpsg, transform))
            return;
        var vpsg = evidence.Vpsg!;
        controller.ObserveAbsolute(new AdaptiveScaleObservation(
            evidence.FrameId,
            DateTimeOffset.UtcNow,
            vpsg.Scale,
            vpsg.Confidence,
            recognition.Result.StructureCandidateMargin,
            AdaptiveScaleObservationSource.Vpsg,
            AdaptiveScaleTransformArbitrator.KeepScale(transform, vpsg.Scale)));
    }

    private AdaptiveScaleInitialStreakState GetInitialStreak(
        AdaptiveScaleKey key,
        AdaptiveScaleStoreEntry? persisted)
    {
        if (_initialStreaks.TryGetValue(key, out var streak))
            return streak;
        streak = new AdaptiveScaleInitialStreakState(key, _options, persisted);
        _initialStreaks[key] = streak;
        return streak;
    }

    private void QueueInitialStreakWrite(AdaptiveScaleInitialStreakSnapshot snapshot)
    {
        Task<AdaptiveScaleStoreEntry> task;
        lock (_pendingWrites)
        {
            task = PersistInitialStreakAfterAsync(_streakWriteTail, snapshot);
            _streakWriteTail = task;
            _pendingWrites.Add(task);
        }
        _ = task.ContinueWith(
            completed => CompleteInitialStreakWrite(snapshot.Key, completed),
            TaskScheduler.Default);
    }

    private async Task<AdaptiveScaleStoreEntry> PersistInitialStreakAfterAsync(
        Task predecessor,
        AdaptiveScaleInitialStreakSnapshot snapshot)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A failed write must not prevent newer state from reaching disk.
        }
        return await _store.RecordInitialStreakAsync(snapshot).ConfigureAwait(false);
    }

    private void CompleteInitialStreakWrite(
        AdaptiveScaleKey key,
        Task<AdaptiveScaleStoreEntry> completed)
    {
        lock (_pendingWrites)
            _pendingWrites.Remove(completed);
        if (!completed.IsFaulted)
            return;
        _log?.Invoke(
            "adaptive initial streak persistence failed",
            new Dictionary<string, object?>
            {
                ["mapId"] = key.MapId,
                ["floor"] = key.FloorKey,
                ["exception"] = completed.Exception?.GetBaseException().ToString()
            });
    }

    public async Task ResetForScaleRecoveryAsync(AdaptiveScaleKey key)
    {
        // Clear runtime arbitration first.  Even if persistence fails, the
        // recovered result in this process must restart from Provisional.
        lock (_stateGate)
        {
            _initialStreaks.Remove(key);
            _controllers.Remove(key);
            if (_activeKey == key)
            {
                _activeKey = null;
                _activeOpenId = 0;
            }
        }

        Task resetTask;
        lock (_pendingWrites)
        {
            resetTask = ResetForScaleRecoveryAfterAsync(
                _streakWriteTail,
                key);
            _streakWriteTail = resetTask;
            _pendingWrites.Add(resetTask);
        }
        try
        {
            await resetTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingWrites)
                _pendingWrites.Remove(resetTask);
        }

        _log?.Invoke(
            "adaptive scale baseline reset after steady recovery",
            new Dictionary<string, object?>
            {
                ["mapId"] = key.MapId,
                ["floor"] = key.FloorKey,
                ["clientWidth"] = key.ClientWidth,
                ["clientHeight"] = key.ClientHeight,
                ["viewportWidth"] = key.ViewportWidth,
                ["viewportHeight"] = key.ViewportHeight
            });
    }

    public async Task<int> ResetMapFloorForPlayerAsync(
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        string floorKey)
    {
        var normalizedFloor = AdaptiveScaleKey.NormalizeFloor(floorKey);
        var updatedAtTicks = mapUpdatedAt.UtcTicks;
        lock (_stateGate)
        {
            foreach (var key in _initialStreaks.Keys.Where(Matches).ToArray())
                _initialStreaks.Remove(key);
            foreach (var key in _controllers.Keys.Where(Matches).ToArray())
                _controllers.Remove(key);
            if (_activeKey is { } active && Matches(active))
            {
                _activeKey = null;
                _activeOpenId = 0;
            }
        }

        Task<int> resetTask;
        lock (_pendingWrites)
        {
            resetTask = ResetMapFloorAfterAsync(
                _streakWriteTail,
                mapId,
                updatedAtTicks,
                normalizedFloor);
            _streakWriteTail = resetTask;
            _pendingWrites.Add(resetTask);
        }
        try
        {
            var removed = await resetTask.ConfigureAwait(false);
            _log?.Invoke(
                "adaptive scale baseline reset by player",
                new Dictionary<string, object?>
                {
                    ["mapId"] = mapId,
                    ["floor"] = normalizedFloor,
                    ["removedEntries"] = removed
                });
            return removed;
        }
        finally
        {
            lock (_pendingWrites)
                _pendingWrites.Remove(resetTask);
        }

        bool Matches(AdaptiveScaleKey key) =>
            key.MapId == mapId
            && key.MapUpdatedAtTicks == updatedAtTicks
            && key.FloorKey == normalizedFloor;
    }

    private async Task ResetForScaleRecoveryAfterAsync(
        Task predecessor,
        AdaptiveScaleKey key)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A previous persistence failure must not preserve a stale scale.
        }
        await _store.ResetAsync(key).ConfigureAwait(false);
    }

    private async Task<int> ResetMapFloorAfterAsync(
        Task predecessor,
        Guid mapId,
        long mapUpdatedAtTicks,
        string floorKey)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A stale write failure must not keep a player-rejected lock.
        }
        return await _store.ResetMapFloorAsync(
            mapId,
            mapUpdatedAtTicks,
            floorKey).ConfigureAwait(false);
    }

    private AdaptiveScaleController GetController(AdaptiveScaleKey key)
    {
        if (_controllers.TryGetValue(key, out var existing))
            return existing;
        var created = new AdaptiveScaleController(key, _options);
        _controllers[key] = created;
        return created;
    }

    private bool TryGetOpenController(
        AdaptiveScaleKey expectedKey,
        long openId,
        out AdaptiveScaleController controller)
    {
        if (_activeKey == expectedKey
            && _activeOpenId == openId
            && _controllers.TryGetValue(expectedKey, out controller!)
            && controller.IsOpen
            && controller.OpenId == openId)
        {
            return true;
        }
        controller = null!;
        return false;
    }

    private AdaptiveStructureDecision HoldRuntimeScale(
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform,
        AdaptiveScaleController controller) =>
        new(
            MapCvRecognitionBuilders.ReplaceTransformAndSource(
                recognition,
                AdaptiveScaleTransformArbitrator.KeepScale(transform, controller.RuntimeScale),
                recognition.Result.Source),
            false,
            false,
            false,
            controller.State);

    private AdaptiveStructureDecision RejectStructureConsensusCore(
        AdaptiveScaleKey expectedKey,
        long openId,
        RuntimeMapRecognition recognition)
    {
        if (!TryGetOpenController(expectedKey, openId, out var controller)
            || recognition.Result.OverlayTransform is not { } transform)
        {
            return new AdaptiveStructureDecision(
                recognition, false, false, false, AdaptiveScaleState.Stable);
        }
        controller.RejectConsensus();
        return HoldRuntimeScale(recognition, transform, controller);
    }

    private bool IsStrongStructure(
        RuntimeMapRecognition recognition,
        AdaptiveScaleInitialEvidence evidence) =>
        evidence.StructureValidated
        && evidence.ScaleIndependentlyEstimated
        && recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
        && !recognition.Result.ReusedLastTransform
        && !recognition.Result.SkippedStructureValidation
        && recognition.Result.LocalizationConfidence >= _options.ReliableConfidence
        && recognition.Result.StructureRejectionReason == MapStructureRejectionReason.None
        && recognition.Result.StructureCandidateMargin >= evidence.RequiredCandidateMargin
        && recognition.Result.OverlayTransform is { AlignmentMode: MapOverlayAlignmentMode.Uniform };

    private bool IsUsableRecoveryStructure(
        RuntimeMapRecognition recognition,
        AdaptiveScaleInitialEvidence evidence) =>
        evidence.StructureValidated
        && evidence.ScaleIndependentlyEstimated
        && recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
        && !recognition.Result.ReusedLastTransform
        && !recognition.Result.SkippedStructureValidation
        && recognition.Result.LocalizationConfidence >= _options.RecoveryConfidence
        && recognition.Result.StructureRejectionReason == MapStructureRejectionReason.None
        && recognition.Result.StructureCandidateMargin >= evidence.RequiredCandidateMargin
        && recognition.Result.OverlayTransform is { AlignmentMode: MapOverlayAlignmentMode.Uniform };

    private bool IsStrongVpsg(
        AdaptiveVpsgEvidence? evidence,
        MapOverlayTransform transform)
    {
        if (evidence is null || !evidence.Validated)
            return false;

        if (RelativeDifference(evidence.Scale, UniformScale(transform)) > _options.FastConsensusRange)
            return false;

        if (string.Equals(evidence.Mode, "Vpsg3", StringComparison.OrdinalIgnoreCase))
        {
            return evidence.Confidence >= _options.ReliableConfidence;
        }

        return evidence.UniqueMatches >= MapVpsgScaleEstimator.MinimumUniqueMatches
            && evidence.Confidence >= _options.VpsgConfidence
            && evidence.ResidualPixels <= MapVpsgScaleEstimator.MaximumResidualPixels
            && evidence.RelativeMad <= MapVpsgScaleEstimator.MaximumRelativeMad;
    }

    private static AdaptiveAlignmentDecision LegacyDecision(RuntimeMapRecognition recognition) =>
        new(
            recognition,
            AdaptiveScaleReliability.Reliable,
            true,
            true,
            true,
            true,
            "Disabled",
            0,
            0,
            AdaptiveScaleReliabilityReason.Disabled,
            0d,
            false);

    private static double UniformScale(MapOverlayTransform transform) =>
        (transform.ScaleX + transform.ScaleY) / 2d;

    private static double RelativeDifference(double left, double right) =>
        Math.Abs(left - right) / Math.Max(Math.Abs(right), 0.000001d);
}
/*
 * 文件职责：AdaptiveScaleCoordinator.Support。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
