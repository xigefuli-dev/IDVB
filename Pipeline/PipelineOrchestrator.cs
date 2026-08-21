// IDVB Remaster Phase 3.1 — Pipeline Orchestrator

using System.Diagnostics;
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Pipeline;

/// <summary>
/// 管线编排器。按序执行 IPipelineStage 列表，
/// 每阶段记录耗时，阶段失败时根据降级链 fallback。
/// </summary>
public sealed class PipelineOrchestrator
{
    private readonly IReadOnlyList<IPipelineStage> _stages;
    private readonly PipelineFallbackChain _fallback;

    public PipelineOrchestrator(IEnumerable<IPipelineStage> stages, PipelineFallbackChain? fallback = null)
    {
        _stages = stages.ToList();
        _fallback = fallback ?? PipelineFallbackChain.Empty;
    }

    /// <summary>
    /// 执行完整管线。返回的 PipelineContext 中包含所有阶段的输出和耗时。
    /// </summary>
    public async Task<PipelineContext> RunAsync(PipelineContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        foreach (var stage in _stages)
        {
            if (ct.IsCancellationRequested)
            {
                context.IsFailed = true;
                context.FailureReason = "Pipeline cancelled before the next stage.";
                break;
            }

            var phaseSw = Stopwatch.StartNew();
            var stageTrace = MapOperationTraceAmbient.StartChild(
                stage.StageName,
                MapOperationWaitKind.Compute);
            var stageStatus = MapOperationSpanStatus.Completed;
            string? stageTerminalReason = null;
            try
            {
                context = await stage.ExecuteAsync(context, ct);
                if (context.IsFailed)
                {
                    stageStatus = MapOperationSpanStatus.Failed;
                    stageTerminalReason = context.FailureReason;
                }
            }
            catch (OperationCanceledException)
            {
                context.IsFailed = true;
                context.FailureReason = $"Stage '{stage.StageName}' cancelled.";
                stageStatus = MapOperationSpanStatus.Cancelled;
                stageTerminalReason = context.FailureReason;
                break;
            }
            catch (Exception ex)
            {
                context.IsFailed = true;
                context.FailureReason = $"Stage '{stage.StageName}' threw: {ex.Message}";
                stageStatus = MapOperationSpanStatus.Failed;
                stageTerminalReason = context.FailureReason;
            }
            finally
            {
                phaseSw.Stop();
                context.RecordPhase(stage.StageName, phaseSw.Elapsed.TotalMilliseconds);
                stageTrace.Complete(stageStatus, stageTerminalReason);
            }

            // Check for non-exception failure and try fallback
            if (context.IsFailed)
            {
                var fallbackStage = _fallback.GetFallback(stage.StageName);
                if (fallbackStage != null)
                {
                    var fbSw = Stopwatch.StartNew();
                    var fallbackTrace = MapOperationTraceAmbient.StartChild(
                        $"{stage.StageName}:fallback",
                        MapOperationWaitKind.Compute);
                    var fallbackStatus = MapOperationSpanStatus.Completed;
                    string? fallbackTerminalReason = null;
                    try
                    {
                        context.IsFailed = false; // reset for fallback attempt
                        context.FailureReason = null;
                        context = await fallbackStage.ExecuteAsync(context, ct);
                        if (context.IsFailed)
                        {
                            fallbackStatus = MapOperationSpanStatus.Failed;
                            fallbackTerminalReason = context.FailureReason;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        context.IsFailed = true;
                        context.FailureReason =
                            $"Fallback for stage '{stage.StageName}' cancelled.";
                        fallbackStatus = MapOperationSpanStatus.Cancelled;
                        fallbackTerminalReason = context.FailureReason;
                    }
                    catch (Exception ex)
                    {
                        context.IsFailed = true;
                        context.FailureReason =
                            $"Fallback for stage '{stage.StageName}' threw: {ex.Message}";
                        fallbackStatus = MapOperationSpanStatus.Failed;
                        fallbackTerminalReason = context.FailureReason;
                    }
                    finally
                    {
                        fbSw.Stop();
                        context.RecordPhase(
                            $"{stage.StageName}:fallback",
                            fbSw.Elapsed.TotalMilliseconds);
                        fallbackTrace.Complete(
                            fallbackStatus,
                            fallbackTerminalReason);
                    }

                    if (!context.IsFailed)
                    {
                        // Keep the failed original attempt. Removing it made
                        // a successful fallback look like the first route and
                        // hid the time spent on the rejected strategy.
                        continue;
                    }
                }
                break;
            }
        }

        sw.Stop();
        context.TotalWallMs = sw.Elapsed.TotalMilliseconds;
        return context;
    }

    /// <summary>
    /// 运行单个阶段（用于子管线或测试）。
    /// </summary>
    public static async Task<PipelineContext> RunStageAsync(
        IPipelineStage stage, PipelineContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        context = await stage.ExecuteAsync(context, ct);
        context.RecordPhase(stage.StageName, sw.Elapsed.TotalMilliseconds);
        return context;
    }
}

/// <summary>
/// 管线降级链：当某阶段失败时，尝试执行备选阶段。
/// </summary>
public sealed class PipelineFallbackChain
{
    private readonly Dictionary<string, IPipelineStage> _fallbacks = new();

    public static PipelineFallbackChain Empty => new();

    public PipelineFallbackChain WithFallback(string stageName, IPipelineStage fallbackStage)
    {
        _fallbacks[stageName] = fallbackStage;
        return this;
    }

    public IPipelineStage? GetFallback(string stageName) =>
        _fallbacks.TryGetValue(stageName, out var s) ? s : null;
}
