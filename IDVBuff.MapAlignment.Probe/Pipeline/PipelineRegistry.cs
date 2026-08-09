using System.Collections.Frozen;

namespace IDVBuff.MapAlignment.Probe.Pipeline;

/// <summary>
/// 策略注册表，按名称查找策略实例。
/// </summary>
public sealed class PipelineRegistry
{
    private readonly FrozenDictionary<string, IPipelineStrategy> _strategies;

    public PipelineRegistry(IEnumerable<IPipelineStrategy> strategies)
    {
        _strategies = strategies.ToFrozenDictionary(
            s => s.StrategyName,
            StringComparer.OrdinalIgnoreCase);
    }

    public IPipelineStrategy? Find(string name) =>
        _strategies.TryGetValue(name, out var strategy) ? strategy : null;

    public IReadOnlyCollection<string> StrategyNames => _strategies.Keys;
}
