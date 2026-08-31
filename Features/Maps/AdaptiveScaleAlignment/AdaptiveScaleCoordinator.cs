namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed partial class AdaptiveScaleCoordinator
{
    private readonly AdaptiveScaleOptions _options;
    private readonly AdaptiveScaleStore _store;
    private readonly Action<string, Dictionary<string, object?>>? _log;
    private readonly object _stateGate = new();
    private readonly Dictionary<AdaptiveScaleKey, AdaptiveScaleController> _controllers = [];
    private readonly Dictionary<AdaptiveScaleKey, AdaptiveScaleInitialStreakState>
        _initialStreaks = [];
    private readonly List<Task> _pendingWrites = [];
    private Task _streakWriteTail = Task.CompletedTask;
    private AdaptiveScaleKey? _activeKey;
    private long _nextOpenId;
    private long _activeOpenId;

    public AdaptiveScaleCoordinator(
        AdaptiveScaleOptions options,
        AdaptiveScaleStore? store = null,
        Action<string, Dictionary<string, object?>>? log = null)
    {
        _options = options;
        _options.Normalize();
        _log = log;
        _store = store ?? new AdaptiveScaleStore(
            warning: (message, exception) => _log?.Invoke(
                message,
                new Dictionary<string, object?>
                {
                    ["exception"] = exception?.GetBaseException().ToString()
                }));
    }

    public bool Enabled => _options.Enabled;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _store.InitializeAsync(cancellationToken);

    public AdaptiveAlignmentDecision EvaluateInitial(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame,
        MapFeatureCacheSource? legacySource,
        AdaptiveScaleInitialEvidence evidence,
        long openId = 0)
    {
        if (!_options.Enabled || recognition.Result.OverlayTransform is not { } transform)
            return LegacyDecision(recognition);

        var key = AdaptiveScaleKey.Create(
            recognition.Map,
            recognition.Result.Floor,
            frame.ClientBounds,
            frame.ViewportBounds);
        var scale = UniformScale(transform);
        var entry = _store.TryGet(key);
        var persistedTrusted = AdaptiveScaleStore.IsTrusted(entry);
        var strongInitial = IsStrongStructure(recognition, evidence);
        var strongVpsg = IsStrongVpsg(evidence.Vpsg, transform);

        lock (_stateGate)
        {
            var effectiveOpenId = openId > 0
                ? openId
                : Interlocked.Increment(ref _nextOpenId);
            var streak = GetInitialStreak(key, entry);
            var streakResult = streak.Observe(
                effectiveOpenId,
                scale,
                recognition.Result.LocalizationConfidence,
                strongInitial,
                DateTimeOffset.UtcNow,
                preserveWhenUnqualified: evidence.StructureValidated
                    && !evidence.ScaleIndependentlyEstimated,
                clusterTolerance: evidence.ScaleClusterTolerance);
            if (streakResult.Changed)
                QueueInitialStreakWrite(streakResult.Snapshot);

            var trusted = streak.IsReliable;
            var controller = GetController(key);
            _activeKey = key;
            _activeOpenId = effectiveOpenId;
            var resumed = controller.BeginOrResumeOpen(
                _activeOpenId,
                trusted ? streak.MedianScale : scale,
                entry?.CalibrationScale,
                trusted,
                requiresRecovery: !strongInitial);
            AddInitialObservations(controller, recognition, transform, evidence);
            if (strongVpsg)
                controller.LockCurrentScale(evidence.Vpsg!.Scale);
            var reliable = controller.IsReliable;
            var render = controller.HasReliableBaseline
                && (resumed || strongVpsg)
                ? MapCvRecognitionBuilders.ReplaceTransformAndSource(
                    recognition,
                    AdaptiveScaleTransformArbitrator.KeepScale(
                        transform,
                        controller.RuntimeScale),
                    recognition.Result.Source)
                : recognition;
            var reason = reliable
                ? strongVpsg
                    ? AdaptiveScaleReliabilityReason.VpsgDirectLock
                    : trusted
                    ? persistedTrusted
                        ? AdaptiveScaleReliabilityReason.TrustedCalibration
                        : AdaptiveScaleReliabilityReason.InitialFiveStreak
                    : AdaptiveScaleReliabilityReason.StructureConsensus
                : AdaptiveScaleReliabilityReason.None;
            var details = AdaptiveScaleDiagnostics.State(key, controller, "initial", scale);
            details["highQualityCount"] = streak.Count;
            details["requiredHighQualityCount"] = _options.RequiredConsecutiveInitialResults;
            details["highQualityCounted"] = streakResult.Counted;
            details["scaleIndependentlyEstimated"] =
                evidence.ScaleIndependentlyEstimated;
            details["highQualityClusterRebuilt"] = streakResult.Rebuilt;
            details["scaleResolutionRatio"] = evidence.ScaleResolutionRatio;
            details["scaleClusterTolerance"] = evidence.ScaleClusterTolerance;
            details["initialScaleRelativeMad"] = streakResult.Snapshot.RelativeMad;
            details["reliabilityReason"] = reason.ToString();
            details["legacySource"] = legacySource?.ToString();
            _log?.Invoke(
                reliable ? "adaptive initial reliable" : "adaptive initial provisional",
                details);
            if (reliable)
            {
                _log?.Invoke(
                    "adaptive floor scale locked",
                    AdaptiveScaleDiagnostics.State(
                        key,
                        controller,
                        reason.ToString(),
                        controller.RuntimeScale));
            }

            return new AdaptiveAlignmentDecision(
                render,
                reliable ? AdaptiveScaleReliability.Reliable : AdaptiveScaleReliability.Provisional,
                reliable,
                reliable,
                reliable,
                true,
                reliable ? "Reliable" : "Provisional",
                streak.Count,
                _options.RequiredConsecutiveInitialResults,
                reason,
                streakResult.Snapshot.RelativeMad,
                streakResult.Rebuilt);
        }
    }

    public AdaptiveOrbDecision EvaluateOrbObservation(
        AdaptiveScaleKey expectedKey,
        long openId,
        MapOverlayTransform candidate,
        double stepScale,
        DateTimeOffset observedAt)
    {
        lock (_stateGate)
        {
            if (!TryGetOpenController(expectedKey, openId, out var controller))
                return new AdaptiveOrbDecision(candidate, false, false, AdaptiveScaleState.Stable);

            controller.ObserveOrbScale(stepScale, observedAt);
            var held = AdaptiveScaleTransformArbitrator.KeepScale(candidate, controller.RuntimeScale);
            var reanchor = RelativeDifference(UniformScale(candidate), controller.RuntimeScale) > 0.0001d;
            return new AdaptiveOrbDecision(
                held,
                controller.State != AdaptiveScaleState.Stable,
                reanchor,
                controller.State);
        }
    }

    public AdaptiveStructureDecision EvaluateStructureObservation(
        AdaptiveScaleKey expectedKey,
        long openId,
        RuntimeMapRecognition recognition,
        long frameId,
        double requiredCandidateMargin,
        AdaptiveScaleObservationSource source = AdaptiveScaleObservationSource.Structure)
    {
        lock (_stateGate)
        {
            if (!TryGetOpenController(expectedKey, openId, out var controller)
                || !expectedKey.Matches(recognition.Map, recognition.Result.Floor)
                || recognition.Result.OverlayTransform is not { } transform)
            {
                return new AdaptiveStructureDecision(
                    recognition, false, false, false, AdaptiveScaleState.Stable);
            }

            var evidence = new AdaptiveScaleInitialEvidence(
                frameId,
                requiredCandidateMargin,
                StructureValidated: true);
            var strong = IsStrongStructure(recognition, evidence);
            var usableRecovery = source == AdaptiveScaleObservationSource.Structure
                && IsUsableRecoveryStructure(recognition, evidence);
            if (!strong && !(controller.State == AdaptiveScaleState.Recovering
                && usableRecovery))
            {
                controller.ObserveStructureFailure();
                return HoldRuntimeScale(recognition, transform, controller);
            }

            var observation = new AdaptiveScaleObservation(
                frameId,
                DateTimeOffset.UtcNow,
                UniformScale(transform),
                recognition.Result.LocalizationConfidence,
                recognition.Result.StructureCandidateMargin,
                source,
                transform);
            var consensus = strong
                ? controller.ObserveAbsolute(observation)
                : controller.ObserveRecoveryAbsolute(observation);
            if (consensus is null)
                return HoldRuntimeScale(recognition, transform, controller);

            var held = HoldRuntimeScale(recognition, transform, controller);
            return held with { PendingConsensus = consensus };
        }
    }

    public AdaptiveStructureDecision CommitStructureConsensus(
        AdaptiveScaleKey expectedKey,
        long openId,
        RuntimeMapRecognition recognition,
        AdaptiveScaleConsensus consensus,
        double requiredCandidateMargin)
    {
        lock (_stateGate)
        {
            var evidence = new AdaptiveScaleInitialEvidence(
                consensus.LatestObservation.FrameId,
                requiredCandidateMargin,
                StructureValidated: true);
            if (!TryGetOpenController(expectedKey, openId, out var controller)
                || !expectedKey.Matches(recognition.Map, recognition.Result.Floor)
                || recognition.Result.OverlayTransform is not { } transform
                || RelativeDifference(UniformScale(transform), consensus.Scale) > _options.Deadband
                || (consensus.IsProvisionalRecovery
                    && controller.HasReliableBaseline)
                || (consensus.IsProvisionalRecovery
                    ? !IsUsableRecoveryStructure(recognition, evidence)
                    : !IsStrongStructure(recognition, evidence)))
            {
                return RejectStructureConsensusCore(
                    expectedKey,
                    openId,
                    recognition);
            }

            var wasReliable = controller.IsReliable;
            var previousScale = controller.RuntimeScale;
            if (consensus.IsProvisionalRecovery)
                controller.CommitProvisionalRecovery(consensus);
            else
                controller.CommitConsensus(consensus);
            var changed = RelativeDifference(previousScale, consensus.Scale) > _options.Deadband;
            var becameReliable = !wasReliable && controller.IsReliable;
            _log?.Invoke(
                consensus.IsProvisionalRecovery
                    ? "adaptive provisional scale recovery committed"
                    : "adaptive scale consensus committed",
                AdaptiveScaleDiagnostics.State(controller.Key, controller, "commit", consensus.Scale));
            return new AdaptiveStructureDecision(
                recognition,
                changed,
                becameReliable,
                true,
                controller.State);
        }
    }

    public AdaptiveStructureDecision RejectStructureConsensus(
        AdaptiveScaleKey expectedKey,
        long openId,
        RuntimeMapRecognition recognition)
    {
        lock (_stateGate)
            return RejectStructureConsensusCore(expectedKey, openId, recognition);
    }

    public int GetStructureProbeInterval(
        AdaptiveScaleKey expectedKey,
        long openId,
        int legacyIntervalMilliseconds)
    {
        lock (_stateGate)
            return TryGetOpenController(expectedKey, openId, out var controller)
                ? controller.ProbeIntervalMilliseconds
                : legacyIntervalMilliseconds;
    }

    public bool TryGetCalibrationSeed(
        AdaptiveScaleKey key,
        out AdaptiveScaleSeedDecision? seed)
    {
        seed = null;
        if (!_options.Enabled)
            return false;
        var entry = _store.TryGet(key);
        if (!AdaptiveScaleStore.IsTrusted(entry))
            return false;
        seed = new AdaptiveScaleSeedDecision(
            entry!.CalibrationScale,
            entry.Confidence,
            entry.RelativeMad,
            AdaptiveScaleSeedSource.Calibration);
        return true;
    }

    public bool IsQualifiedInitialResult(
        RuntimeMapRecognition recognition,
        double requiredCandidateMargin,
        bool structureValidated = true)
    {
        if (!_options.Enabled)
            return true;
        return IsStrongStructure(
            recognition,
            new AdaptiveScaleInitialEvidence(
                0,
                requiredCandidateMargin,
                structureValidated));
    }

    /// <summary>
    /// 已锁定地图的快速对齐（仅对齐 / 缓存命中验证）使用的宽松质量门槛。
    /// 与 <see cref="IsQualifiedInitialResult"/> 的差异仅在置信度下限：
    /// 两个门槛由 AdaptiveScaleOptions 配置；当前默认调教值为 0.65。
    /// 适用场景有侧门扫描先验或落盘缓存支撑，低置信但已验证的结果仍可采纳，
    /// 不应在调用方再写死旧的 0.82 门槛。
    /// </summary>
    public bool IsUsableInitialResult(
        RuntimeMapRecognition recognition,
        double requiredCandidateMargin,
        bool structureValidated = true)
    {
        if (!_options.Enabled)
            return true;
        return IsUsableRecoveryStructure(
            recognition,
            new AdaptiveScaleInitialEvidence(
                0,
                requiredCandidateMargin,
                structureValidated));
    }

    public bool TryGetPreferredSeed(
        AdaptiveScaleKey key,
        long openId,
        out AdaptiveScaleSeedDecision? seed)
    {
        seed = null;
        if (!_options.Enabled)
            return false;
        lock (_stateGate)
        {
            if (_controllers.TryGetValue(key, out var controller)
                && controller.IsOpen
                && controller.OpenId == openId
                && controller.IsReliable)
            {
                seed = new AdaptiveScaleSeedDecision(
                    controller.RuntimeScale,
                    1d,
                    0d,
                    AdaptiveScaleSeedSource.Runtime);
                return true;
            }
        }
        return TryGetCalibrationSeed(key, out seed);
    }

}
/*
 * 文件职责：AdaptiveScaleCoordinator。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
