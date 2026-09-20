// IDVB Remaster Phase 0.4 — Core Model

namespace IDVBuff.Core.Models;

/// <summary>
/// 管线上下文 — 各阶段间传递数据的可变属性袋。
/// 每个阶段读取其关心的属性，处理后写回。阶段可标记失败以触发降级链。
/// </summary>
public class PipelineContext
{
    /// <summary>唯一定义本次管线执行的 ID。</summary>
    public string ExecutionId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>当前是否已标记失败。</summary>
    public bool IsFailed { get; set; }

    /// <summary>失败原因（仅在 IsFailed 时有效）。</summary>
    public string? FailureReason { get; set; }

    /// <summary>各阶段的耗时记录（毫秒），按阶段名称索引。</summary>
    public Dictionary<string, double> PhaseTimings { get; init; } = new();

    /// <summary>视口区域（归一化坐标）。</summary>
    public ViewportBounds? Viewport { get; set; }

    /// <summary>识别的楼层。</summary>
    public FloorLevel? DetectedFloor { get; set; }

    /// <summary>地图识别结果。</summary>
    public string? IdentifiedMapId { get; set; }
    public double? IdentificationConfidence { get; set; }

    /// <summary>对齐输出。</summary>
    public AlignmentOutput? Alignment { get; set; }

    /// <summary>对齐结果（新版 Core 模型）。</summary>
    public AlignmentResult? AlignmentResult { get; set; }

    /// <summary>当前活跃的分辨率预设名。</summary>
    public string? ActiveResolutionPreset { get; set; }

    /// <summary>总耗时（毫秒），管线结束后由编排器设置。</summary>
    public double TotalWallMs { get; set; }

    /// <summary>标记上下文为失败并记录原因。</summary>
    public PipelineContext Fail(string reason)
    {
        IsFailed = true;
        FailureReason = reason;
        return this;
    }

    /// <summary>获取阶段耗时（毫秒），不存在则返回 0。</summary>
    public double GetPhaseMs(string stageName) =>
        PhaseTimings.TryGetValue(stageName, out var ms) ? ms : 0;

    /// <summary>记录阶段耗时。</summary>
    public void RecordPhase(string stageName, double ms) =>
        PhaseTimings[stageName] = ms;
}

/// <summary>视口边界（归一化 0..1 坐标）。</summary>
public readonly record struct ViewportBounds(double X, double Y, double Width, double Height);

/// <summary>门检测结果。</summary>
public sealed class GateDetection
{
    public double Score { get; init; }
    public double TemplateScale { get; init; }
    public ViewportBounds ScreenBounds { get; init; }
    public string? Role { get; set; }
}

/// <summary>楼层标识。</summary>
public enum FloorLevel { Unknown = 0, First = 1, Second = 2 }

/// <summary>对齐输出。</summary>
public sealed class AlignmentOutput
{
    public bool IsAccepted { get; set; }
    public double Confidence { get; set; }
    public double ScaleX { get; set; }
    public double ScaleY { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public int ReferenceWidth { get; set; }
    public int ReferenceHeight { get; set; }
    public string? RejectionReason { get; set; }
}
