namespace IDVBuff.MapAlignment.Probe.Output;

/// <summary>
/// 统一的 ProbeResult JSON schema，所有策略输出均使用此格式。
/// 可被 AI agent 直接解析。
/// </summary>
public sealed record ProbeResult
{
    public string Strategy { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? MapId { get; init; }
    public string? MapDisplayName { get; init; }
    public double Confidence { get; init; }
    public PhaseTimings Phases { get; init; } = new();
    public TransformInfo? Transform { get; init; }
    public IReadOnlyList<CandidateInfo> Candidates { get; init; } = [];
    public string? FailureReason { get; init; }
    public long? ImageWidth { get; init; }
    public long? ImageHeight { get; init; }
    public object? Extra { get; init; }
}

public sealed class PhaseTimings
{
    public double LoadMs { get; init; }
    public double GateCreateMatchImageMs { get; init; }
    public double GateDetectMs { get; init; }
    public double CatalogLoadMs { get; init; }
    public double FingerprintBuildMs { get; init; }
    public double GeometryRankMs { get; init; }
    public double ReferenceLoadMs { get; init; }
    public double StructureWallMs { get; init; }
    public double TotalWallMs { get; init; }
}

public sealed class TransformInfo
{
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public string AlignmentMode { get; init; } = "Uniform";
}

public sealed class CandidateInfo
{
    public string? MapId { get; init; }
    public string? MapDisplayName { get; init; }
    public string? FloorKey { get; init; }
    public double VectorError { get; init; }
    public double Score { get; init; }
    public double? EstimatedScaleX { get; init; }
    public double? EstimatedScaleY { get; init; }
    public GateInfo? MainGate { get; init; }
    public GateInfo? SideGate { get; init; }
    public StructureCandidateInfo? Structure { get; init; }
    public string? TransformSource { get; init; }
    public bool? IsSelected { get; init; }
    public double? FinalConfidence { get; init; }
}

public sealed class GateInfo
{
    public double Score { get; init; }
    public double Scale { get; init; }
    public GateBoundsInfo? Bounds { get; init; }
}

public sealed class GateBoundsInfo
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed class StructureCandidateInfo
{
    public bool Accepted { get; init; }
    public double Confidence { get; init; }
    public double? Scale { get; init; }
    public double? OffsetX { get; init; }
    public double? OffsetY { get; init; }
    public double BestScore { get; init; }
    public double CandidateMargin { get; init; }
    public string? Rejection { get; init; }
    public double WallMs { get; init; }
    public double SearchMs { get; init; }
    public double RefineMs { get; init; }
    public double ReferencePreprocessMs { get; init; }
    public double LivePreprocessMs { get; init; }
    public double DistanceMapMs { get; init; }
    public double ReferenceDiskMs { get; init; }
    public bool ReferenceCacheHit { get; init; }
    public double DownscaleFactor { get; init; }
    public IReadOnlyList<CandidateDetail> TopCandidates { get; init; } = [];
}

public sealed class CandidateDetail
{
    public double Scale { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double ChamferPixels { get; init; }
    public double EdgeCoverage { get; init; }
    public int InlierCount { get; init; }
    public int RawScore { get; init; }
    public double FinalScore { get; init; }
    public bool IsWithinValidBounds { get; init; }
}
