// IDVB Remaster Phase 3.1 — Core Contract

using IDVBuff.Core.Models;

namespace IDVBuff.Core.Contracts;

/// <summary>
/// 管线阶段统一接口。每个阶段接收 PipelineContext，
/// 执行其职责范围内的处理，并返回更新后的上下文。
/// </summary>
public interface IPipelineStage
{
    /// <summary>执行该管线阶段。</summary>
    Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct);

    /// <summary>阶段名称，用于诊断日志和性能追踪。</summary>
    string StageName { get; }
}
