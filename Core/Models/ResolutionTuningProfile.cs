// IDVB Remaster Phase 0.4 — Core Model

namespace IDVBuff.Core.Models;

/// <summary>
/// 分辨率调优档案 — 封装特定分辨率下的参数覆盖值。
/// </summary>
public sealed class ResolutionTuningProfile
{
    public string Name { get; init; } = string.Empty;
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int Dpi { get; init; } = 120;

    // 结构配准参数覆盖
    public double? MaximumChamferPixels { get; init; }
    public double? MinimumEdgeCoverage { get; init; }
    public double? MinimumOccupancyCoverage { get; init; }
    public double? MinimumCandidateMargin { get; init; }
    public double? EdgeDistanceTolerancePixels { get; init; }
    public int? FastCoarseMaxDimension { get; init; }
    public int? FastCoarseDownsampleFactor { get; init; }
    public double? ScaleSearchRadius { get; init; }

    // 门检测参数覆盖
    public double? GateTemplateThreshold { get; init; }
    public double? VectorErrorTolerance { get; init; }
}
