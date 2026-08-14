namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed record AdaptiveScaleInitialSample(
    double Scale,
    double Confidence,
    DateTimeOffset ObservedAt);

internal sealed record AdaptiveScaleInitialStreakSnapshot(
    AdaptiveScaleKey Key,
    IReadOnlyList<AdaptiveScaleInitialSample> Samples,
    int ConsecutiveCount,
    double MedianScale,
    double MinimumConfidence,
    double RelativeMad,
    DateTimeOffset LastValidatedAt);

internal sealed record AdaptiveScaleInitialStreakResult(
    bool Changed,
    bool Counted,
    bool Rebuilt,
    AdaptiveScaleInitialStreakSnapshot Snapshot);

internal sealed class AdaptiveScaleInitialStreakState
{
    private readonly AdaptiveScaleKey _key;
    private readonly int _requiredCount;
    private readonly double _clusterTolerance;
    private readonly List<AdaptiveScaleInitialSample> _samples = [];
    private long? _lastCountedOpenId;

    public AdaptiveScaleInitialStreakState(
        AdaptiveScaleKey key,
        AdaptiveScaleOptions options,
        AdaptiveScaleStoreEntry? persisted = null)
    {
        _key = key;
        _requiredCount = options.RequiredConsecutiveInitialResults;
        _clusterTolerance = options.InitialScaleClusterTolerance;
        if (persisted?.InitialSamples is { Count: > 0 })
        {
            _samples.AddRange(persisted.InitialSamples
                .Where(IsValid)
                .TakeLast(_requiredCount));
        }
    }

    public int Count => _samples.Count;
    public bool IsReliable => Count >= _requiredCount;
    public double MedianScale => Median(_samples.Select(item => item.Scale));

    public AdaptiveScaleInitialStreakResult Observe(
        long openId,
        double scale,
        double confidence,
        bool qualified,
        DateTimeOffset observedAt)
    {
        if (_lastCountedOpenId == openId)
            return Result(changed: false, counted: false, rebuilt: false, observedAt);

        _lastCountedOpenId = openId;
        if (!qualified || !double.IsFinite(scale) || scale <= 0d)
        {
            var changed = _samples.Count > 0;
            _samples.Clear();
            return Result(changed, counted: true, rebuilt: false, observedAt);
        }

        var sample = new AdaptiveScaleInitialSample(scale, confidence, observedAt);
        var candidateScales = _samples.Select(item => item.Scale)
            .Append(scale)
            .TakeLast(_requiredCount)
            .ToArray();
        var candidateMedian = Median(candidateScales);
        var rebuilt = _samples.Count > 0
            && candidateScales.Any(item =>
                RelativeDifference(item, candidateMedian) > _clusterTolerance);
        if (rebuilt)
            _samples.Clear();
        _samples.Add(sample);
        while (_samples.Count > _requiredCount)
            _samples.RemoveAt(0);
        return Result(changed: true, counted: true, rebuilt, observedAt);
    }

    public AdaptiveScaleInitialStreakSnapshot Snapshot(DateTimeOffset observedAt) =>
        CreateSnapshot(observedAt);

    private AdaptiveScaleInitialStreakResult Result(
        bool changed,
        bool counted,
        bool rebuilt,
        DateTimeOffset observedAt) =>
        new(changed, counted, rebuilt, CreateSnapshot(observedAt));

    private AdaptiveScaleInitialStreakSnapshot CreateSnapshot(DateTimeOffset observedAt)
    {
        var median = MedianScale;
        var mad = _samples.Count == 0
            ? 0d
            : Median(_samples.Select(item => Math.Abs(item.Scale - median))) / median;
        return new AdaptiveScaleInitialStreakSnapshot(
            _key,
            _samples.ToArray(),
            Count,
            median,
            _samples.Count == 0 ? 0d : _samples.Min(item => item.Confidence),
            mad,
            observedAt);
    }

    private static bool IsValid(AdaptiveScaleInitialSample sample) =>
        double.IsFinite(sample.Scale)
        && sample.Scale > 0d
        && double.IsFinite(sample.Confidence)
        && sample.Confidence >= 0d;

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return 0d;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static double RelativeDifference(double left, double right) =>
        Math.Abs(left - right) / Math.Max(Math.Abs(right), 0.000001d);
}
