// IDVB Remaster Phase 3 — Pipeline Factory (DI-based)

using IDVBuff.Core.Contracts;
using IDVBuff.Pipeline.Stages;
using Microsoft.Extensions.DependencyInjection;

namespace IDVBuff.Pipeline;

/// <summary>
/// 管线工厂 — 通过 DI 容器创建预配置的扫描/对齐管线实例。
/// </summary>
public sealed class PipelineFactory
{
    private readonly IServiceProvider _services;

    public PipelineFactory(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>创建扫描管线。</summary>
    public PipelineOrchestrator CreateScanPipeline()
    {
        var stages = new IPipelineStage[]
        {
            _services.GetRequiredService<CaptureStage>(),
            _services.GetRequiredService<FloorDetectStage>(),
            _services.GetRequiredService<GateDetectStage>(),
            _services.GetRequiredService<MapIdentifyStage>(),
        };

        var fallback = new PipelineFallbackChain()
            .WithFallback("map_identify", _services.GetRequiredService<MapIdentifyStage>());

        return new PipelineOrchestrator(stages, fallback);
    }

    /// <summary>创建对齐管线。</summary>
    public PipelineOrchestrator CreateAlignmentPipeline()
    {
        var stages = new IPipelineStage[]
        {
            _services.GetRequiredService<StrategySelectStage>(),
            _services.GetRequiredService<TransformCalcStage>(),
            _services.GetRequiredService<RefineStage>(),
            _services.GetRequiredService<ValidateStage>(),
            _services.GetRequiredService<ProjectStage>(),
        };

        var fallback = new PipelineFallbackChain()
            .WithFallback("strategy_select", _services.GetRequiredService<StrategySelectStage>());

        return new PipelineOrchestrator(stages, fallback);
    }
}
