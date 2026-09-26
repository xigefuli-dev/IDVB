using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 单个楼层对齐/试探的终止状态。
/// </summary>
public enum FloorAlignmentAttemptOutcome
{
    /// <summary>完整通过该通道所有结构验收规则，且存在有效变换。</summary>
    Accepted,
    /// <summary>完成计算，但结构证据明确不通过（如 WeakAbsoluteScore、NoCandidate 等）。</summary>
    Rejected,
    /// <summary>低结构楼层结构可用但跨帧证据等待中，不可作为结构失败判定。</summary>
    PendingEvidence,
    /// <summary>预算耗尽、早期终止或资源不足以判定。</summary>
    Inconclusive,
    /// <summary>操作已过期或被新操作取代。</summary>
    Superseded,
    /// <summary>计算异常或资源缺失。</summary>
    Error
}

/// <summary>
/// 单楼层试探结果，封装该候选的所有证据、诊断与延迟副作用。
/// </summary>
public sealed record FloorAlignmentAttemptResult
{
    public required string FloorKey { get; init; }
    public required FloorAlignmentAttemptOutcome Outcome { get; init; }
    public required MapRecognitionAttempt Attempt { get; init; }
    public RuntimeMapRecognition? AlignedRecognition { get; init; }
    public MapStructureRejectionReason RejectionReason { get; init; }
    public string? FailureReason { get; init; }
    public MapFeatureCacheKey? PendingRepairCacheKey { get; init; }
    public bool ResetRecoveredScaleState { get; init; }
    public required MapScanDiagnostics Diagnostics { get; init; }
    public double Confidence { get; init; }
    public bool LowStructurePending { get; init; }
}

/// <summary>
/// 跨楼层恢复裁定结果类型。
/// </summary>
public enum FloorRecoveryResolution
{
    /// <summary>未触发恢复（如首选楼层已成功，或为用户精确手动选择）。</summary>
    NotAttempted,
    /// <summary>有且仅有唯一候选楼层完整通过结构验收。</summary>
    SingleAccepted,
    /// <summary>多个候选楼层同时通过结构验收，存在歧义，不盲选。</summary>
    Ambiguous,
    /// <summary>存在候选处于跨帧证据等待中，不能宣布其它候选唯一。</summary>
    PendingEvidence,
    /// <summary>预算耗尽或部分候选未完成。</summary>
    Inconclusive,
    /// <summary>所有候选均明确被结构性拒绝。</summary>
    AllRejected,
    /// <summary>操作已过期。</summary>
    Superseded
}

/// <summary>
/// 跨楼层恢复裁定详情。
/// </summary>
public sealed record FloorRecoveryDecision
{
    public required FloorRecoveryResolution Resolution { get; init; }
    public FloorAlignmentAttemptResult? Winner { get; init; }
    public IReadOnlyList<FloorAlignmentAttemptResult> Attempts { get; init; } = [];
    public string? Reason { get; init; }
}
