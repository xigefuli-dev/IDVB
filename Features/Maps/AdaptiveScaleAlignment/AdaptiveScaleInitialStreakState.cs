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
        if (persisted is { ScaleEvidenceVersion: >= 1 }
            && persisted.InitialSamples is { Count: > 0 })
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
        DateTimeOffset observedAt,
        bool preserveWhenUnqualified = false)
    {
        if (_lastCountedOpenId == openId)
            return Result(changed: false, counted: false, rebuilt: false, observedAt);

        _lastCountedOpenId = openId;
        if (!qualified && preserveWhenUnqualified)
            return Result(
                changed: false,
                counted: false,
                rebuilt: false,
                observedAt);
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
/*
 * 文件职责：AdaptiveScaleInitialStreakState。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
