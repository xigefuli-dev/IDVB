// IDVB Real CLI — 识别结果输出模型

using System.Text.Json.Serialization;

namespace IDVBuff.RealCLI.Output;

/// <summary>
/// 单次识别会话的完整输出。
/// </summary>
public sealed class RealCliSessionResult
{
    public string ImagePath { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? StatusMessage { get; init; }

    public RealCliRecognitionOutput? Recognition { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>后台扫描状态（Idle / Running / CompletedIdentified / CompletedAmbiguous / CompletedFailed）。</summary>
    public string BackgroundScanStatus { get; init; } = "Idle";

    /// <summary>后台扫描是否已完成且结果尚未被开图消费。</summary>
    public bool IsBackgroundScanCompleted { get; init; }

    /// <summary>对齐会话状态（用于诊断"扫描成功但仅对齐不动"问题）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealCliAlignmentSessionOutput? AlignmentSession { get; init; }

    /// <summary>叠加窗口操作事件（来自 RecordingOverlayWindow）。</summary>
    public List<string> OverlayEvents { get; init; } = new();

    /// <summary>扫描管线的阶段耗时（毫秒）。</summary>
    public Dictionary<string, double> PhaseTimings { get; init; } = new();

    /// <summary>扫描管线各阶段耗时（键=阶段名，值=毫秒）。侧门路径可能为 null。</summary>
    public Dictionary<string, double>? ScanPhaseTimings { get; init; }

    /// <summary>对齐细分耗时与策略分类。</summary>
    public RealCliDiagnosticsOutput? Diagnostics { get; init; }

    /// <summary>总耗时（毫秒）。</summary>
    public double TotalWallMs { get; init; }

    /// <summary>仅对齐阶段各阶段耗时（毫秒，键=阶段名）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? AlignmentPhaseTimings { get; init; }

    /// <summary>阶段①锁定（首次扫描）耗时（毫秒）。</summary>
    public double ScanLockWallMs { get; init; }

    /// <summary>阶段③仅对齐耗时（毫秒）。</summary>
    public double AlignmentWallMs { get; init; }

    /// <summary>锁定分类：FullLock / LockNoTransform / IdentityOnly / NoLock。</summary>
    public string LockStatus { get; init; } = "NoLock";

    /// <summary>仅对齐是否产出有效锁定（结果保持完整 transform）。</summary>
    public bool AlignmentSucceeded { get; init; }

    /// <summary>请求的候选位置（1-based，未指定为 null）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedCandidate { get; init; }

    /// <summary>本次扫描的候选数量（无候选为 0）。</summary>
    public int CandidateCount { get; init; }

    /// <summary>候选地图列表（候选确认时非空）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RealCliCandidateChoiceOutput>? CandidateChoices { get; init; }

    /// <summary>日志条目摘要。</summary>
    public List<RealCliLogEntrySummary> LogEntries { get; init; } = new();

    /// <summary>Fatal 异常（如果发生）。</summary>
    public string? FatalError { get; init; }
}

/// <summary>
/// 识别结果的核心数据（从 RuntimeMapRecognition 提取）。
/// </summary>
public sealed class RealCliRecognitionOutput
{
    public string MapId { get; init; } = string.Empty;
    public string MapDisplayName { get; init; } = string.Empty;
    public string Floor { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string RecognitionSource { get; init; } = string.Empty;
    public bool HasAllRequiredAnchorEvidence { get; init; }
    public double GeometryMargin { get; init; }
    public string FloorImagePath { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealCliTransformOutput? Transform { get; init; }
}

/// <summary>
/// 叠加变换参数（从 MapOverlayTransform 提取）。
/// </summary>
public sealed class RealCliTransformOutput
{
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
}

/// <summary>
/// 日志条目的简要摘要。
/// </summary>
public sealed class RealCliLogEntrySummary
{
    public string Category { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double ElapsedMs { get; init; }
}

/// <summary>
/// 对齐细分耗时与策略分类（从 MapScanDiagnostics 提取）。
/// </summary>
public sealed class RealCliDiagnosticsOutput
{
    // ── 核心时序（毫秒）──

    public double PreprocessMs { get; init; }
    public double GateDetectionMs { get; init; }
    public double GeometryMs { get; init; }
    public double CacheMs { get; init; }
    public double StructureSearchMs { get; init; }
    public double StructureRefineMs { get; init; }
    public double OverlayMs { get; init; }
    public double TotalMs { get; init; }

    // ── 策略分类 ──

    public int GateCandidateCount { get; init; }
    public string EvidenceKind { get; init; } = "None";
    public bool StructureAttempted { get; init; }
    public bool StructureAccepted { get; init; }
    public string SearchStage { get; init; } = "None";

    // ── 质量指标 ──

    public double StructureBestScore { get; init; }
    public double StructureCandidateMargin { get; init; }
}

/// <summary>
/// 对齐会话诊断输出（从 MapAlignmentSession 提取）。
/// 用于诊断"扫描成功但仅对齐不动"问题。
/// </summary>
public sealed class RealCliAlignmentSessionOutput
{
    public string MapId { get; init; } = string.Empty;
    public string FloorKey { get; init; } = "1f";
    public string Mode { get; init; } = "None";
    public double SideEntranceScanPriorConfidence { get; init; }
    public bool HasGatePairLock { get; init; }
    public double BaselineGateScale { get; init; }
    public double LastConfidence { get; init; }
    public double LastBestScore { get; init; }
    public int ConsecutiveRejections { get; init; }
    public bool LastStructureAccepted { get; init; }
    public string LastStructureFailureReason { get; init; } = string.Empty;
    public int ConsecutiveStructureFailures { get; init; }

    /// <summary>根据当前会话状态，下一次仅对齐应走的路由。</summary>
    public string PredictedAlignmentRoute { get; init; } = "Unknown";
}

/// <summary>
/// 候选地图选择项摘要（从 MapRecognitionChoice 提取，用于诊断候选歧义）。
/// </summary>
public sealed class RealCliCandidateChoiceOutput
{
    public string MapId { get; init; } = string.Empty;
    public string MapDisplayName { get; init; } = string.Empty;
    public string Floor { get; init; } = string.Empty;
    public double RawConfidence { get; init; }
    public bool IsReferenceOnly { get; init; }
    public string EvidenceLabel { get; init; } = string.Empty;
    public int PreferredOrder { get; init; }
}

/// <summary>
/// 批量运行汇总。
/// </summary>
public sealed class RealCliBatchSummary
{
    public int TotalFiles { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public double AverageConfidence { get; init; }
    public double AverageWallMs { get; init; }
    public List<RealCliSessionResult> Results { get; init; } = new();
}
