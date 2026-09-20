// IDVB Remaster Phase 3.1 — Alignment Pipeline Context

using IDVBuff.Core.Models;

namespace IDVBuff.Pipeline;

/// <summary>
/// 对齐管线的上下文 — 携带对齐流程各阶段的输入输出。
/// 注意：部分属性使用 object 类型以桥接 Core/Pipeline 与主项目之间的类型边界。
/// 这些 object 属性由 SessionOrchestrator（主项目侧）在管线启动前填充真实类型。
/// </summary>
public sealed class AlignmentPipelineContext : PipelineContext
{
    /// <summary>目标地图 ID。</summary>
    public string? MapId { get; set; }

    /// <summary>目标楼层。</summary>
    public FloorLevel Floor { get; set; } = FloorLevel.First;

    /// <summary>检测到的门列表。</summary>
    public List<GateDetection> DetectedGates { get; init; } = [];

    /// <summary>选中的对齐策略名称。</summary>
    public string? SelectedStrategy { get; set; }

    /// <summary>策略降级次数。</summary>
    public int StrategyFallbackCount { get; set; }

    /// <summary>已尝试的策略列表。</summary>
    public List<string> AttemptedStrategies { get; init; } = [];

    /// <summary>对齐结果。</summary>
    public AlignmentResult? Result { get; set; }

    /// <summary>变换精修耗时（毫秒）。</summary>
    public double RefineMs => GetPhaseMs("refine");

    /// <summary>验证耗时（毫秒）。</summary>
    public double ValidateMs => GetPhaseMs("validate");

    // ── 桥接属性：由主项目侧的 SessionOrchestrator 填充真实类型 ──

    /// <summary>
    /// 目标地图记录（桥接：主项目侧填充 MapRecord 实例）。
    /// TransformCalcStage 将此透传给 IMapAligner.AlignAsync()。
    /// </summary>
    public object? MapRecordRaw { get; set; }

    /// <summary>
    /// 视口截图（桥接：主项目侧填充 Mat 实例，来自 CapturedGameFrame.Image）。
    /// </summary>
    public object? ScreenshotRaw { get; set; }

    /// <summary>
    /// 结构配准请求（桥接：主项目侧填充 MapStructureRegistrationRequest 实例）。
    /// RefineStage 将此透传给 IStructureRegistrar.Register()。
    /// </summary>
    public object? StructureRequestRaw { get; set; }

    /// <summary>
    /// 记录已尝试的策略（避免重复尝试）。
    /// </summary>
    public bool HasTriedStrategy(string name) => AttemptedStrategies.Contains(name);

    public void MarkStrategyAttempted(string name)
    {
        if (!AttemptedStrategies.Contains(name))
            AttemptedStrategies.Add(name);
    }
}
