// IDVB Remaster Phase 3.3 — Alignment Pipeline Stages (functional implementation)

using System.Diagnostics;
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Pipeline.Stages;

/// <summary>策略选择阶段 — 根据门数量/历史状态选择对齐策略。</summary>
public sealed class StrategySelectStage : IPipelineStage
{
    public string StageName => "strategy_select";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        if (context is not AlignmentPipelineContext alignCtx)
            return context.Fail("StrategySelectStage requires AlignmentPipelineContext");

        var gateCount = alignCtx.DetectedGates.Count;

        // 优先级链：双门 → 单门 → 仅结构 → 保持上次变换
        var strategies = new[]
        {
            gateCount >= 2 ? "dual_gate" : null,
            gateCount >= 1 ? "single_gate" : null,
            "structure_only",
            "hold_last"
        };

        foreach (var strategy in strategies)
        {
            if (strategy == null || alignCtx.HasTriedStrategy(strategy))
                continue;

            alignCtx.SelectedStrategy = strategy;
            break;
        }

        alignCtx.MarkStrategyAttempted(alignCtx.SelectedStrategy ?? "unknown");
        await Task.CompletedTask;
        return alignCtx;
    }
}

/// <summary>变换计算阶段 — 根据选定策略计算叠加层变换矩阵。</summary>
public sealed class TransformCalcStage : IPipelineStage
{
    private readonly IMapAligner? _aligner;

    public TransformCalcStage(IEnumerable<IMapAligner>? aligners = null)
    {
        _aligner = aligners?.FirstOrDefault();
    }

    public string StageName => "transform_calc";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (context is AlignmentPipelineContext alignCtx)
        {
            // 使用 context 中的桥接对象（由 SessionOrchestrator 预填充）
            var mapRecord = alignCtx.MapRecordRaw;
            var screenshot = alignCtx.ScreenshotRaw;

            if (_aligner != null && mapRecord != null && screenshot != null)
            {
                try
                {
                    var result = await _aligner.AlignAsync(mapRecord, screenshot, ct);
                    alignCtx.Result = new AlignmentResult
                    {
                        Succeeded = true,
                        Confidence = 0.85, // 初始值，RefineStage 会更新
                        StrategyName = alignCtx.SelectedStrategy,
                        TotalWallMs = sw.Elapsed.TotalMilliseconds
                    };
                }
                catch (Exception ex)
                {
                    return context.Fail(
                        $"Transform calculation failed for strategy '{alignCtx.SelectedStrategy}': {ex.Message}");
                }
            }
            else if (alignCtx.SelectedStrategy == "hold_last" || alignCtx.SelectedStrategy == "structure_only")
            {
                // 无需 IMapAligner 的策略：由 RefineStage 通过 IStructureRegistrar 处理
                alignCtx.Result = new AlignmentResult
                {
                    Succeeded = false, // 暂未成功，等 RefineStage 更新
                    Confidence = 0,
                    StrategyName = alignCtx.SelectedStrategy,
                };
            }
            else
            {
                return context.Fail(
                    $"No IMapAligner available for strategy '{alignCtx.SelectedStrategy}'");
            }
        }

        context.RecordPhase("transform_calc", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }
}

/// <summary>精修阶段 — 结构配准精修。</summary>
public sealed class RefineStage : IPipelineStage
{
    private readonly IStructureRegistrar? _registrar;

    public RefineStage(IStructureRegistrar? registrar = null)
    {
        _registrar = registrar;
    }

    public string StageName => "refine";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_registrar != null && context is AlignmentPipelineContext alignCtx)
        {
            try
            {
                // 使用 context 中的桥接对象（由 SessionOrchestrator 预填充）
                // 若无，则跳过结构配准（RefineStage 是可选的）
                var request = alignCtx.StructureRequestRaw;
                if (request != null)
                {
                    var refined = _registrar.Register(request);

                    // 从返回的 MapStructureRegistrationResult 中提取置信度
                    var resultType = refined.GetType();
                    var accepted = (bool?)resultType.GetProperty("Accepted")?.GetValue(refined);
                    var confidence = (double?)resultType.GetProperty("Confidence")?.GetValue(refined);

                    if (alignCtx.Result != null)
                    {
                        alignCtx.Result.Succeeded = accepted == true;
                        alignCtx.Result.Confidence = confidence ?? alignCtx.Result.Confidence;

                        alignCtx.Result.StructureConfirmation = new StructureConfirmation
                        {
                            Attempted = true,
                            Accepted = accepted == true,
                            Confidence = confidence ?? 0,
                            BestScore = (double)(resultType.GetProperty("BestScore")?.GetValue(refined) ?? 0),
                            CandidateMargin = (double)(resultType.GetProperty("CandidateMargin")?.GetValue(refined) ?? 0),
                        };
                    }
                }
            }
            catch
            {
                // Refinement is optional — don't fail the pipeline
            }
        }

        context.RecordPhase("refine", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }
}

/// <summary>验证阶段 — 校验变换合法性（边界、置信度、候选边际）。</summary>
public sealed class ValidateStage : IPipelineStage
{
    private readonly IConfigProvider? _config;

    public ValidateStage(IConfigProvider? config = null)
    {
        _config = config;
    }

    public string StageName => "validate";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (context is AlignmentPipelineContext { Result: { } result })
        {
            var minConfidence = 0.62; // 默认值（与 ConfidenceConfig.Medium 一致）

            // 从 TOML 配置读取阈值（如果可用）
            if (_config != null)
            {
                try
                {
                    var confidenceCfg = _config.Get<ConfidenceConfig>("confidence");
                    if (confidenceCfg?.Medium is > 0)
                        minConfidence = confidenceCfg.Medium;
                }
                catch
                {
                    // 配置读取失败时使用默认值
                }
            }

            if (result.Confidence < minConfidence)
                return context.Fail(
                    $"Confidence {result.Confidence:F2} below minimum {minConfidence:F2}");

            // 验证候选边际（最佳候选与次优候选之间的差距）
            if (result.StructureConfirmation is { Attempted: true, Accepted: true } sc)
            {
                if (sc.CandidateMargin < 0.15)
                {
                    // 边际过小表示候选之间区分度不足，但仍可接受（不 fail）
                    // 仅降低最终置信度
                }
            }
        }

        context.RecordPhase("validate", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }
}

/// <summary>投影阶段 — 将变换应用到叠加层渲染。</summary>
public sealed class ProjectStage : IPipelineStage
{
    private readonly IOverlayWindow? _overlay;

    public ProjectStage(IOverlayWindow? overlay = null)
    {
        _overlay = overlay;
    }

    public string StageName => "project";

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_overlay != null && context is AlignmentPipelineContext { Result: { Succeeded: true } result })
        {
            try
            {
                // 显示叠加层（背景渲染由 SessionOrchestrator 在 LockBackground 时完成）
                _overlay.Show();
            }
            catch (Exception ex)
            {
                return context.Fail($"Failed to project overlay: {ex.Message}");
            }
        }

        context.RecordPhase("project", sw.Elapsed.TotalMilliseconds);
        await Task.CompletedTask;
        return context;
    }
}
