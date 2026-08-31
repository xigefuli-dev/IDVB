using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private AdaptiveScaleCoordinator _adaptiveScale = null!;
    private long _adaptiveFrameId;
    private AdaptiveScaleKey? _lastReliableAdaptiveKey;
    private AdaptiveScaleKey? _primaryFloorAdaptiveKey;
    private readonly object _manualFloorScaleLockGate = new();
    private readonly Dictionary<ManualFloorScaleLockKey, double>
        _manualFloorScaleLocks = [];

    private readonly record struct ManualFloorScaleLockKey(
        Guid MatchId,
        Guid MapId,
        long MapUpdatedAtTicks,
        string FloorKey,
        int ClientWidth,
        int ClientHeight,
        int ViewportWidth,
        int ViewportHeight)
    {
        public static ManualFloorScaleLockKey Create(
            MapMatchSnapshot match,
            MapRecord map,
            string floorKey,
            MapCacheResolutionSignature resolution) =>
            new(
                match.MatchId,
                map.Id,
                map.UpdatedAt.UtcTicks,
                AdaptiveScaleKey.NormalizeFloor(floorKey),
                resolution.ClientWidth,
                resolution.ClientHeight,
                resolution.ViewportWidth,
                resolution.ViewportHeight);
    }

    private void InitializeAdaptiveScale()
    {
        var options = _config.Get<AdaptiveScaleOptions>("adaptive_scale");
        _adaptiveScale = new AdaptiveScaleCoordinator(
            options,
            log: (message, details) => _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                message,
                details: details));
    }

    private Task InitializeAdaptiveScaleAsync() =>
        _adaptiveScale.InitializeAsync(_lifetimeCts.Token);

    private Task<AdaptiveAlignmentDecision> EvaluateAdaptiveInitialAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame,
        MapScanDiagnostics? diagnostics,
        MapFeatureCacheSource? explicitSource = null)
    {
        // Manual runtime-scale locking is independent from adaptive-scale
        // arbitration, but it still needs the exact capture geometry so that
        // a lock cannot leak into another resolution. Record that context for
        // every accepted alignment, including provisional/disabled adaptive
        // results that are intentionally excluded from automatic cache writes.
        RememberAlignmentCaptureContext(frame);
        var source = explicitSource ?? ResolveLegacyScaleSource(recognition, frame);
        var evidence = CreateAdaptiveInitialEvidence(recognition, diagnostics);
        return Task.FromResult(_adaptiveScale.EvaluateInitial(
            recognition,
            frame,
            source,
            evidence,
            _gameMapToggleState.Version));
    }

    private AdaptiveScaleInitialEvidence CreateAdaptiveInitialEvidence(
        RuntimeMapRecognition recognition,
        MapScanDiagnostics? diagnostics)
    {
        AdaptiveVpsgEvidence? vpsg = null;
        if (diagnostics is { ScaleBootstrapValidated: true })
        {
            vpsg = new AdaptiveVpsgEvidence(
                diagnostics.ScaleBootstrapValidated,
                diagnostics.ScaleBootstrapScale,
                diagnostics.ScaleBootstrapConfidence,
                diagnostics.ScaleBootstrapUniqueMatches,
                diagnostics.ScaleBootstrapPairVotes,
                diagnostics.ScaleBootstrapResidualPixels,
                diagnostics.ScaleBootstrapRelativeMad);
        }
        return new AdaptiveScaleInitialEvidence(
            Interlocked.Increment(ref _adaptiveFrameId),
            CreateStructureTuningForFloor(
                recognition.Map,
                recognition.Result.Floor,
                CreateEffectiveStructureTuning()).MinimumCandidateMargin,
            recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
                && !recognition.Result.ReusedLastTransform
                && !recognition.Result.SkippedStructureValidation
                && recognition.Result.StructureRejectionReason
                    == MapStructureRejectionReason.None
                && (diagnostics is null
                    || (diagnostics.StructureAccepted
                        && string.IsNullOrWhiteSpace(
                            diagnostics.StructureHardGateFailure))),
            vpsg,
            ScaleIndependentlyEstimated: diagnostics is null
                || string.IsNullOrWhiteSpace(diagnostics.LowStructureRoute)
                || LowStructureScaleEvidenceRules.IsIndependentScaleRoute(
                    diagnostics.LowStructureRoute),
            ScaleClusterTolerance: diagnostics is { LowStructureScaleResolutionRatio: > 0d }
                ? LowStructureScaleEvidenceRules.ResolveClusterTolerance(
                    diagnostics.LowStructureScaleResolutionRatio)
                : null,
            ScaleResolutionRatio: diagnostics?.LowStructureScaleResolutionRatio ?? 0d);
    }

    private MapFeatureCacheSource? ResolveLegacyScaleSource(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return null;
        var key = CreateAlignmentCacheKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        return _mapFeatureCacheRepository.TryGet(key, out var entry)
            ? entry?.Scale.Source
            : null;
    }

    private bool CanUseAdaptiveReliableSession(
        MapAlignmentSession session,
        AdaptiveScaleKey key) =>
        _adaptiveScale.CanUseAsReliableSession(
            session,
            key,
            _gameMapToggleState.Version);

    private static AdaptiveScaleKey CreateAdaptiveScaleKey(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey) =>
        AdaptiveScaleKey.Create(
            map,
            floorKey,
            frame.ClientBounds,
            frame.ViewportBounds);

    private bool TryGetActiveAdaptiveKey(
        RuntimeMapRecognition recognition,
        out AdaptiveScaleKey key)
    {
        if (_adaptiveScale.TryGetActiveKey(out key)
            && key.Matches(recognition.Map, recognition.Result.Floor))
        {
            return true;
        }
        key = default;
        return false;
    }

    private void RememberAdaptiveReliableKey(
        RuntimeMapRecognition recognition,
        bool primary)
    {
        if (!TryGetActiveAdaptiveKey(recognition, out var key))
            return;
        _lastReliableAdaptiveKey = key;
        if (primary)
            _primaryFloorAdaptiveKey = key;
    }

    private void ClearAdaptiveSessionKeys()
    {
        _lastReliableAdaptiveKey = null;
        _primaryFloorAdaptiveKey = null;
    }

    private async Task ResetAdaptiveScaleAfterSteadyRecoveryAsync(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey)
    {
        var key = CreateAdaptiveScaleKey(frame, map, floorKey);
        try
        {
            await _adaptiveScale.ResetForScaleRecoveryAsync(key);
        }
        catch (Exception exception)
        {
            // ResetAsync clears the in-memory store before persisting.  Keep
            // rendering the recovered result, but retain a diagnostic if the
            // stale on-disk entry could not be removed for the next process.
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                $"Steady 尺度恢复后的自适应基线持久化重置失败 · floor={floorKey}",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["exception"] = exception.GetBaseException().Message
                });
        }
        if (_lastReliableAdaptiveKey == key)
            _lastReliableAdaptiveKey = null;
        if (_primaryFloorAdaptiveKey == key)
            _primaryFloorAdaptiveKey = null;
    }

    private bool IsAdaptiveTransformConfirmed(
        OrbTrackingContext context,
        MapOverlayTransform transform) =>
        _adaptiveScale.IsConfirmedTransform(
            context.AdaptiveKey,
            context.Toggle.Version,
            transform);

    private bool TryLockCurrentAdaptiveScale(
        RuntimeMapRecognition recognition,
        double scale) =>
        TryGetActiveAdaptiveKey(recognition, out var key)
        && _adaptiveScale.TryLockCurrentScale(
            key,
            _gameMapToggleState.Version,
            scale);

    private bool RememberManualFloorScaleLock(
        RuntimeMapRecognition recognition,
        double scale)
    {
        var match = _matchSession.Snapshot;
        if (!match.IsStarted
            || !double.IsFinite(scale)
            || scale <= 0d
            || _lastAlignmentResolution is not { } resolution)
        {
            return false;
        }

        var key = ManualFloorScaleLockKey.Create(
            match,
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        lock (_manualFloorScaleLockGate)
            _manualFloorScaleLocks[key] = scale;
        return true;
    }

    private bool TryGetManualFloorScaleLock(
        MapMatchSnapshot match,
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        out double scale)
    {
        if (!match.IsStarted)
        {
            scale = 0d;
            return false;
        }

        var key = ManualFloorScaleLockKey.Create(
            match,
            map,
            floorKey,
            GetResolution(frame));
        lock (_manualFloorScaleLockGate)
            return _manualFloorScaleLocks.TryGetValue(key, out scale);
    }

    private void ClearManualFloorScaleLocks()
    {
        lock (_manualFloorScaleLockGate)
            _manualFloorScaleLocks.Clear();
    }

    private bool IsAdaptiveScaleEnabled => _adaptiveScale.Enabled;

    private bool IsAdaptiveInitialScaleQualified(
        MapRecognitionAttempt? attempt,
        MapStructureRegistrationTuning structureTuning) =>
        attempt?.Recognition is { } recognition
        && _adaptiveScale.IsQualifiedInitialResult(
            recognition,
            structureTuning.MinimumCandidateMargin,
            attempt.StructureAccepted);

    /// <summary>
    /// 已锁定地图快速对齐路径（侧门种子 / 缓存命中验证）的宽松质量门槛：
    /// 置信度只要达到 RecoveryConfidence，不再要求 ReliableConfidence。
    /// 这些路径已有侧门扫描先验或落盘缓存支撑，放宽后不再因 0.73~0.81 的
    /// 边缘置信度被迫转回完整恢复（重复结构搜索 + VPSG + 全局恢复）。
    /// </summary>
    private bool IsAdaptiveInitialScaleUsable(
        MapRecognitionAttempt? attempt,
        MapStructureRegistrationTuning structureTuning) =>
        attempt?.Recognition is { } recognition
        && _adaptiveScale.IsUsableInitialResult(
            recognition,
            structureTuning.MinimumCandidateMargin,
            attempt.StructureAccepted);

    private bool AdaptiveScaleRequiresWideSearch(OrbTrackingContext context) =>
        _adaptiveScale.RequiresWideScaleSearch(
            context.AdaptiveKey,
            context.Toggle.Version);

    private void NotifyAdaptiveStructureFailure(OrbTrackingContext context) =>
        _adaptiveScale.ObserveStructureFailure(
            context.AdaptiveKey,
            context.Toggle.Version);

    private int GetAdaptiveStructureProbeInterval(
        OrbTrackingContext context,
        int legacyMilliseconds) =>
        _adaptiveScale.GetStructureProbeInterval(
            context.AdaptiveKey,
            context.Toggle.Version,
            legacyMilliseconds);

    private AdaptiveOrbDecision EvaluateAdaptiveOrb(
        OrbTrackingContext context,
        MapOverlayTransform transform,
        double stepScale) =>
        _adaptiveScale.EvaluateOrbObservation(
            context.AdaptiveKey,
            context.Toggle.Version,
            transform,
            stepScale,
            DateTimeOffset.UtcNow);

    private AdaptiveStructureDecision EvaluateAdaptiveStructure(
        OrbTrackingContext context,
        CapturedGameFrame frame,
        RuntimeMapRecognition recognition,
        long frameId)
    {
        var margin = CreateStructureTuningForFloor(
            recognition.Map,
            recognition.Result.Floor,
            CreateEffectiveStructureTuning()).MinimumCandidateMargin;
        var observed = _adaptiveScale.EvaluateStructureObservation(
            context.AdaptiveKey,
            context.Toggle.Version,
            recognition,
            frameId,
            margin);
        if (observed.PendingConsensus is not { } consensus)
            return observed;

        var finalAttempt = RunAdaptiveFixedScaleTranslation(
            frame,
            recognition,
            consensus.Scale);
        if (finalAttempt.Recognition is not { } finalized)
        {
            return _adaptiveScale.RejectStructureConsensus(
                context.AdaptiveKey,
                context.Toggle.Version,
                observed.Recognition);
        }
        return _adaptiveScale.CommitStructureConsensus(
            context.AdaptiveKey,
            context.Toggle.Version,
            finalized,
            consensus,
            margin);
    }

    private void EndAdaptiveMapOpen(string reason) =>
        _adaptiveScale.EndActiveOpen(reason);

    private void SuspendActiveAdaptiveFloor(string reason) =>
        _adaptiveScale.SuspendActiveFloor(
            _gameMapToggleState.Version,
            reason);

    private Task DrainAdaptiveScaleAsync() => _adaptiveScale.DrainAsync();
}
/*
 * 文件职责：SessionOrchestrator.AdaptiveScale。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
