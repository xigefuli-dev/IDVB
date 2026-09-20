namespace IDVBuff.Features.Maps;

public sealed class MapCandidateStabilityTracker
{
    private MapSimilarityTransform? _candidate;
    private int _count;
    private readonly List<MapSimilarityTransform> _history = [];

    public int Count => _count;
    public IReadOnlyList<MapSimilarityTransform> History =>
        _history.ToArray();

    /// <summary>
    /// Observes a candidate transform using the configured position tolerance.
    /// </summary>
    public bool Observe(MapSimilarityTransform candidate) =>
        Observe(candidate, MapSessionRules.PositionTolerancePixels);

    public bool Observe(MapSimilarityTransform candidate, double tolerancePixels)
    {
        if (!candidate.IsValid)
        {
            Reset();
            return false;
        }

        if (_candidate is null
            || Math.Abs(_candidate.TranslationX - candidate.TranslationX) > tolerancePixels
            || Math.Abs(_candidate.TranslationY - candidate.TranslationY) > tolerancePixels
            || Math.Abs((_candidate.Scale / candidate.Scale) - 1d) > MapSessionRules.ScaleToleranceRatio
            || Math.Abs(_candidate.RotationDegrees - candidate.RotationDegrees) > MapSessionRules.RotationToleranceDegrees)
        {
            _candidate = candidate;
            _count = 1;
            _history.Clear();
        }
        else
        {
            _candidate = candidate;
            _count++;
        }
        _history.Add(candidate);
        if (_history.Count > MapSessionRules.MaxHistoryEntries)
            _history.RemoveAt(0);
        return _count >= MapSessionRules.MediumConfidenceConfirmationFrames;
    }

    public void Reset()
    {
        _candidate = null;
        _count = 0;
        _history.Clear();
    }
}

/// <summary>
/// Debounces passive floor observations before they are allowed to invalidate
/// a trusted alignment. A missing or matching observation breaks the streak.
/// </summary>
public sealed class MapFloorChangeTracker
{
    private string? _candidateFloor;

    public int Count { get; private set; }
    public string? CandidateFloor => _candidateFloor;

    public bool Observe(
        string? lockedFloor,
        string? observedFloor,
        int requiredFrames = 3)
    {
        if (string.IsNullOrWhiteSpace(lockedFloor)
            || string.IsNullOrWhiteSpace(observedFloor)
            || string.Equals(
                lockedFloor,
                observedFloor,
                StringComparison.Ordinal))
        {
            Reset();
            return false;
        }

        if (!string.Equals(
                _candidateFloor,
                observedFloor,
                StringComparison.Ordinal))
        {
            _candidateFloor = observedFloor;
            Count = 1;
        }
        else
        {
            Count++;
        }
        return Count >= Math.Max(1, requiredFrames);
    }

    public void Reset()
    {
        _candidateFloor = null;
        Count = 0;
    }
}
/*
 * 文件职责：MapSessionModels.Stability。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
