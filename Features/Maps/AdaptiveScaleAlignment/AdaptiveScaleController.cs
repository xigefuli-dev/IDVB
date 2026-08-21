namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed class AdaptiveScaleController
{
    private readonly AdaptiveScaleOptions _options;
    private readonly AdaptiveScaleObservationWindow _window;
    private readonly AdaptiveScaleRuntimeTracker _runtime = new();
    private DateTimeOffset? _challengeStartedAt;
    private int _fixedScaleFailures;
    private bool _hasReliableScale;

    public AdaptiveScaleController(AdaptiveScaleKey key, AdaptiveScaleOptions options)
    {
        Key = key;
        _options = options;
        _window = new AdaptiveScaleObservationWindow(options);
    }

    public AdaptiveScaleKey Key { get; }
    public AdaptiveScaleState State { get; private set; } = AdaptiveScaleState.Provisional;
    public double RuntimeScale => _runtime.RuntimeScale;
    public double? CalibrationScale => _runtime.CalibrationScale;
    public bool HasRuntimeZoom => _runtime.HasRuntimeZoom;
    public bool IsOpen { get; private set; }
    public long OpenId { get; private set; }
    public bool HasReliableBaseline => _hasReliableScale;
    public bool IsReliable => State == AdaptiveScaleState.Stable && _hasReliableScale;
    public int ProbeIntervalMilliseconds => State == AdaptiveScaleState.Stable
        ? _options.StableProbeMilliseconds
        : _options.ActiveProbeMilliseconds;

    public bool BeginOrResumeOpen(
        long openId,
        double initialScale,
        double? calibrationScale,
        bool trusted,
        bool requiresRecovery = false)
    {
        if (IsOpen && OpenId == openId)
            return true;
        IsOpen = true;
        OpenId = openId;
        _runtime.Begin(initialScale, calibrationScale);
        _window.Clear();
        _fixedScaleFailures = 0;
        _challengeStartedAt = null;
        _hasReliableScale = trusted;
        State = trusted
            ? AdaptiveScaleState.Stable
            : requiresRecovery
                ? AdaptiveScaleState.Recovering
                : AdaptiveScaleState.Provisional;
        return false;
    }

    public void EndOpen(long openId)
    {
        if (!IsOpen || OpenId != openId)
            return;
        IsOpen = false;
        OpenId = 0;
        _runtime.EndOpen();
        _window.Clear();
        _fixedScaleFailures = 0;
        _challengeStartedAt = null;
        _hasReliableScale = false;
        State = AdaptiveScaleState.Provisional;
    }

    public void ObserveOrbScale(double stepScale, DateTimeOffset now)
    {
        if (!IsOpen || !double.IsFinite(stepScale) || stepScale <= 0d)
            return;
        var relativeDelta = Math.Abs(stepScale - 1d);
        if (relativeDelta <= _options.Deadband || State == AdaptiveScaleState.Recovering)
            return;
        if (relativeDelta >= _options.Deadband && State == AdaptiveScaleState.Stable)
            EnterChallenged(now);
    }

    public AdaptiveScaleConsensus? ObserveAbsolute(AdaptiveScaleObservation observation)
    {
        if (!IsOpen)
            return null;
        if (!_window.Add(observation))
            return null;

        _fixedScaleFailures = 0;
        var delta = RelativeDifference(observation.Scale, RuntimeScale);
        if (State == AdaptiveScaleState.Stable && delta > _options.ChallengeThreshold)
            EnterChallenged(observation.ObservedAt, clearWindow: false);

        if (_window.HasCompetingClusters())
        {
            _window.Clear();
            if (State == AdaptiveScaleState.Challenged)
                State = _hasReliableScale ? AdaptiveScaleState.Stable : AdaptiveScaleState.Provisional;
            return null;
        }

        var consensus = _window.TryGetConsensus();
        if (consensus is null)
        {
            if (State == AdaptiveScaleState.Challenged
                && _challengeStartedAt is { } started
                && observation.ObservedAt - started
                    >= TimeSpan.FromMilliseconds(_options.ChallengeTimeoutMilliseconds))
            {
                _window.Clear();
                State = _hasReliableScale ? AdaptiveScaleState.Stable : AdaptiveScaleState.Recovering;
            }
            return null;
        }

        return consensus;
    }

    public AdaptiveScaleConsensus? ObserveRecoveryAbsolute(
        AdaptiveScaleObservation observation)
    {
        if (!IsOpen
            || State != AdaptiveScaleState.Recovering
            || _hasReliableScale)
            return null;
        if (!_window.Add(observation))
            return null;
        if (_window.HasCompetingClusters())
        {
            _window.Clear();
            return null;
        }
        return _window.TryGetRecoveryConsensus();
    }

    public void CommitConsensus(AdaptiveScaleConsensus consensus)
    {
        if (!IsOpen)
            return;
        var changed = RelativeDifference(consensus.Scale, RuntimeScale) > _options.Deadband;
        _runtime.SetRuntime(consensus.Scale, _hasReliableScale && changed);
        _hasReliableScale = true;
        State = AdaptiveScaleState.Stable;
        _challengeStartedAt = null;
        _window.Clear();
    }

    public bool LockCurrentScale(double scale)
    {
        if (!IsOpen || !double.IsFinite(scale) || scale <= 0d)
            return false;
        _runtime.SetRuntime(scale, isRuntimeZoom: false);
        _hasReliableScale = true;
        State = AdaptiveScaleState.Stable;
        _fixedScaleFailures = 0;
        _challengeStartedAt = null;
        _window.Clear();
        return true;
    }

    public void CommitProvisionalRecovery(AdaptiveScaleConsensus consensus)
    {
        if (!IsOpen || _hasReliableScale)
            return;
        _runtime.SetRuntime(consensus.Scale, isRuntimeZoom: false);
        _hasReliableScale = false;
        State = AdaptiveScaleState.Provisional;
        _fixedScaleFailures = 0;
        _challengeStartedAt = null;
        _window.Clear();
    }

    public void RejectConsensus()
    {
        if (!IsOpen)
            return;
        _window.Clear();
        ObserveStructureFailure();
    }

    public void ObserveStructureFailure()
    {
        if (!IsOpen)
            return;
        _fixedScaleFailures++;
        if (_fixedScaleFailures < 2)
            return;
        State = AdaptiveScaleState.Recovering;
        _challengeStartedAt = null;
        _window.Clear();
    }

    public bool CanUseReliableScale(double scale) =>
        IsOpen
        && IsReliable
        && RelativeDifference(scale, RuntimeScale) <= _options.Deadband;

    public void SetCalibrationScale(double scale) => _runtime.SetCalibration(scale);

    private void EnterChallenged(DateTimeOffset now, bool clearWindow = true)
    {
        State = AdaptiveScaleState.Challenged;
        _challengeStartedAt ??= now;
        if (clearWindow)
            _window.Clear();
    }

    private static double RelativeDifference(double left, double right) =>
        !double.IsFinite(left) || !double.IsFinite(right) || left <= 0d || right <= 0d
            ? double.PositiveInfinity
            : Math.Abs(left - right) / Math.Max(Math.Abs(right), 0.000001d);
}
/*
 * 文件职责：AdaptiveScaleController。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
