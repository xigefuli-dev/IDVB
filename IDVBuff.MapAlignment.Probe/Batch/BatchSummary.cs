namespace IDVBuff.MapAlignment.Probe.Batch;

/// <summary>
/// 批量评估的聚合汇总统计。
/// </summary>
public sealed class BatchSummary
{
    public string Strategy { get; init; } = string.Empty;
    public int TotalFiles { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public double AverageConfidence { get; init; }
    public double MinimumConfidence { get; init; }
    public double MaximumConfidence { get; init; }
    public double AverageTotalWallMs { get; init; }
    public IReadOnlyList<BatchFileResult> Results { get; init; } = [];
    public IReadOnlyList<PerMapStats> PerMapStats { get; init; } = [];
    public ConfidenceDistribution ConfidenceDistribution { get; init; } = new();
}

public sealed class BatchFileResult
{
    public string File { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? MapId { get; init; }
    public string? MapDisplayName { get; init; }
    public double Confidence { get; init; }
    public double TotalWallMs { get; init; }
    public string? FailureReason { get; init; }
}

public sealed class PerMapStats
{
    public string MapId { get; init; } = string.Empty;
    public string MapDisplayName { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public int Succeeded { get; init; }
    public double AverageConfidence { get; init; }
}

public sealed class ConfidenceDistribution
{
    public int Bucket90_100 { get; init; }
    public int Bucket80_90 { get; init; }
    public int Bucket70_80 { get; init; }
    public int Bucket50_70 { get; init; }
    public int Bucket0_50 { get; init; }
}
