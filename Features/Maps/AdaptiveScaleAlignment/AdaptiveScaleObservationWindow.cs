namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed class AdaptiveScaleObservationWindow
{
    private readonly AdaptiveScaleOptions _options;
    private readonly List<AdaptiveScaleObservation> _observations = [];

    public AdaptiveScaleObservationWindow(AdaptiveScaleOptions options) => _options = options;

    public int Count => _observations.Count;

    public bool Add(AdaptiveScaleObservation observation)
    {
        Prune(observation.ObservedAt);
        if (!double.IsFinite(observation.Scale) || observation.Scale <= 0d)
            return false;
        if (_observations.Any(item => item.FrameId == observation.FrameId
            && item.Source == observation.Source))
            return false;
        var sameSource = _observations
            .Where(item => item.Source == observation.Source)
            .ToArray();
        var latest = sameSource.LastOrDefault();
        if (latest is not null
            && observation.ObservedAt - latest.ObservedAt
                < TimeSpan.FromMilliseconds(_options.MinimumObservationSpacingMilliseconds))
            return false;

        if (sameSource.Length >= 2)
        {
            var previous = sameSource[^2];
            var priorDirection = (latest!.Scale - previous.Scale) / previous.Scale;
            var nextDirection = (observation.Scale - latest.Scale) / latest.Scale;
            if (Math.Abs(priorDirection) > _options.Deadband
                && Math.Abs(nextDirection) > _options.Deadband
                && Math.Sign(priorDirection) != Math.Sign(nextDirection))
            {
                _observations.Clear();
            }
        }

        _observations.Add(observation);
        while (_observations.Count > _options.MaximumObservations)
            _observations.RemoveAt(0);
        return true;
    }

    public AdaptiveScaleConsensus? TryGetConsensus()
    {
        if (_observations.Count < 3)
            return null;

        var structure = _observations
            .Where(item => item.Source == AdaptiveScaleObservationSource.Structure)
            .GroupBy(item => item.FrameId)
            .Select(group => group.Last())
            .ToArray();
        var vpsg = _observations
            .Where(item => item.Source == AdaptiveScaleObservationSource.Vpsg)
            .GroupBy(item => item.FrameId)
            .Select(group => group.Last())
            .ToArray();
        AdaptiveScaleObservation[] selected;
        if (TryQualify(structure, 3, _options.ConsensusClusterRange, out _))
        {
            selected = structure;
        }
        else
        {
            var fast = structure.TakeLast(2).Concat(vpsg.TakeLast(1)).ToArray();
            if (structure.Length < 2
                || vpsg.Length < 1
                || !TryQualify(fast, 3, _options.FastConsensusRange, out _))
            {
                return null;
            }
            selected = fast;
        }

        var ordered = selected.OrderBy(item => item.Scale).ToArray();
        var median = Median(ordered.Select(item => item.Scale));
        var confidence = Median(ordered.Select(item => item.Confidence));
        var relativeMad = Median(ordered.Select(item => Math.Abs(item.Scale - median))) / median;
        var range = (ordered[^1].Scale - ordered[0].Scale) / median;
        if (confidence < _options.ReliableConfidence)
            return null;

        return new AdaptiveScaleConsensus(
            median,
            confidence,
            relativeMad,
            range,
            structure.Length,
            vpsg.Length,
            selected.MaxBy(item => item.ObservedAt)!);
    }

    public AdaptiveScaleConsensus? TryGetRecoveryConsensus()
    {
        var structure = _observations
            .Where(item => item.Source == AdaptiveScaleObservationSource.Structure)
            .GroupBy(item => item.FrameId)
            .Select(group => group.Last())
            .TakeLast(_options.RecoveryStructureCount)
            .ToArray();
        if (!TryQualify(
                structure,
                _options.RecoveryStructureCount,
                _options.ConsensusClusterRange,
                out var median))
        {
            return null;
        }

        var confidence = Median(structure.Select(item => item.Confidence));
        if (confidence < _options.RecoveryConfidence)
            return null;
        var relativeMad = Median(structure.Select(item =>
            Math.Abs(item.Scale - median))) / median;
        var range = (structure.Max(item => item.Scale)
            - structure.Min(item => item.Scale)) / median;
        return new AdaptiveScaleConsensus(
            median,
            confidence,
            relativeMad,
            range,
            structure.Length,
            0,
            structure.MaxBy(item => item.ObservedAt)!,
            IsProvisionalRecovery: true);
    }

    public bool HasCompetingClusters()
    {
        if (_observations.Count < 4)
            return false;
        var ordered = _observations.OrderBy(item => item.Scale).ToArray();
        var largestGap = ordered.Zip(ordered.Skip(1), (left, right) =>
            (right.Scale - left.Scale) / left.Scale).DefaultIfEmpty().Max();
        return largestGap > _options.ConsensusClusterRange;
    }

    public void Clear() => _observations.Clear();

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromMilliseconds(_options.ObservationWindowMilliseconds);
        _observations.RemoveAll(item => item.ObservedAt < cutoff);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private bool TryQualify(
        IReadOnlyList<AdaptiveScaleObservation> observations,
        int minimumCount,
        double maximumRange,
        out double median)
    {
        median = 0d;
        if (observations.Count < minimumCount)
            return false;
        median = Median(observations.Select(item => item.Scale));
        var center = median;
        var relativeMad = Median(observations.Select(item =>
            Math.Abs(item.Scale - center))) / center;
        var range = (observations.Max(item => item.Scale)
            - observations.Min(item => item.Scale)) / center;
        return relativeMad <= _options.ConsensusRelativeMad
            && range <= maximumRange;
    }
}
/*
 * 文件职责：AdaptiveScaleObservationWindow。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
