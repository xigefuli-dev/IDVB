using IDVBuff.MapAlignment.Probe.Output;

namespace IDVBuff.MapAlignment.Probe.Pipeline;

/// <summary>
/// 统一管线策略接口。每个策略封装一种识别方案
/// （双门对齐、侧门扫描、仅门检测、仅楼层识别等）。
/// </summary>
public interface IPipelineStrategy
{
    /// <summary>策略名称，用于 CLI 路由和输出标识。</summary>
    string StrategyName { get; }

    /// <summary>执行策略并返回统一的 ProbeResult。</summary>
    Task<ProbeResult> RunAsync(ProbeContext context, CancellationToken ct);
}
