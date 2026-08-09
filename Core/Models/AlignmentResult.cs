// IDVB Remaster Phase 0.4 — Core Model

namespace IDVBuff.Core.Models;

/// <summary>
/// 对齐流程的最终产出。
/// </summary>
public sealed class AlignmentResult
{
    /// <summary>对齐是否成功（允许 RefineStage 更新）。</summary>
    public bool Succeeded { get; set; }

    /// <summary>对齐置信度（0..1）（允许 RefineStage 更新）。</summary>
    public double Confidence { get; set; }

    /// <summary>变换参数。</summary>
    public AlignmentTransform? Transform { get; init; }

    /// <summary>证据类型。</summary>
    public string? EvidenceKind { get; init; }

    /// <summary>使用的策略名称。</summary>
    public string? StrategyName { get; init; }

    /// <summary>各阶段耗时记录（毫秒）。</summary>
    public Dictionary<string, double> PhaseTimings { get; init; } = new();

    /// <summary>总耗时（毫秒）。</summary>
    public double TotalWallMs { get; set; }

    /// <summary>失败/拒绝对齐的原因。</summary>
    public string? RejectionReason { get; init; }

    /// <summary>结构配准确认结果（若启用）（允许 RefineStage 更新）。</summary>
    public StructureConfirmation? StructureConfirmation { get; set; }
}

/// <summary>对齐变换参数。</summary>
public sealed class AlignmentTransform
{
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public string AlignmentMode { get; init; } = "Uniform";
    public double MaximumResidualPixels { get; init; }
    public bool IsExactFit { get; init; }
}

/// <summary>结构配准确认结果。</summary>
public sealed class StructureConfirmation
{
    public bool Attempted { get; init; }
    public bool Accepted { get; init; }
    public double Confidence { get; init; }
    public double BestScore { get; init; }
    public double SecondScore { get; init; }
    public double CandidateMargin { get; init; }
    public string? RejectionReason { get; init; }
}
