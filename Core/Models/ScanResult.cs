// IDVB Remaster Phase 0.4 — Core Model

namespace IDVBuff.Core.Models;

/// <summary>
/// 扫描流程的最终产出。
/// </summary>
public sealed class ScanResult
{
    /// <summary>扫描是否成功。</summary>
    public bool Succeeded { get; init; }

    /// <summary>识别出的地图 ID。</summary>
    public string? MapId { get; init; }

    /// <summary>地图显示名称。</summary>
    public string? MapDisplayName { get; init; }

    /// <summary>识别的楼层。</summary>
    public FloorLevel Floor { get; init; }

    /// <summary>综合置信度（0..1）。</summary>
    public double Confidence { get; init; }

    /// <summary>证据类型。</summary>
    public string? EvidenceKind { get; init; }

    /// <summary>识别来源。</summary>
    public string? Source { get; init; }

    /// <summary>候选图列表（排名从高到低）。</summary>
    public IReadOnlyList<MapCandidate> Candidates { get; init; } = [];

    /// <summary>各阶段耗时记录（毫秒）。</summary>
    public Dictionary<string, double> PhaseTimings { get; init; } = new();

    /// <summary>总耗时（毫秒）。</summary>
    public double TotalWallMs { get; init; }

    /// <summary>失败原因（仅 Succeeded=false 时有效）。</summary>
    public string? FailureReason { get; init; }
}

/// <summary>候选地图条目。</summary>
public sealed class MapCandidate
{
    public int Rank { get; init; }
    public string MapId { get; init; } = string.Empty;
    public string MapDisplayName { get; init; } = string.Empty;
    public string FloorKey { get; init; } = "1f";
    public double VectorError { get; init; }
    public double Score { get; init; }
    public double EstimatedScaleX { get; init; }
    public double EstimatedScaleY { get; init; }
    public double MainGateScore { get; init; }
    public double SideGateScore { get; init; }
    public bool SelectedForConfirmation { get; init; }
}
