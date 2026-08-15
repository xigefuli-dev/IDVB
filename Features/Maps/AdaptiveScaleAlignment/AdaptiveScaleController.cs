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
