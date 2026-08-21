namespace IDVBuff.Features.Maps;

/// <summary>
/// Serializable, replayable explanation of the separated geometry, evidence,
/// and lock confidence scores.
/// </summary>
public sealed record MapStructureConfidenceBreakdown
{
    public double ChamferPixels { get; init; }
    public double ChamferQuality { get; init; }
    public double EdgeCoverage { get; init; }
    public double OccupancyCoverage { get; init; }
    public int ConsistentPartitions { get; init; }
    public double PartitionQuality { get; init; }
    public double StructureQuality { get; init; }
    public double? FeatureConsensus { get; init; }
    public double CandidateSeparation { get; init; }
    public double? RefinementQuality { get; init; }
    public double BoundsAndPrior { get; init; }
    public double GeometricFitQuality { get; init; }
    public double EvidenceConfidence { get; init; }
    /// <summary>
    /// Diagnostic score from the proposed geometry-dominant lock formula.
    /// It is intentionally not used for runtime lock gating because the
    /// existing thresholds were calibrated against <see cref="LockConfidence"/>.
    /// </summary>
    public double GeometricLockConfidence { get; init; }
    public double LockConfidence { get; init; }
    public string? LowEvidenceReason { get; init; }
    public string? HardGateFailure { get; init; }
    public double EffectiveWeight { get; init; }
    public double FinalScore { get; init; }
}

public static class MapStructureConfidenceCalculator
{
    public static MapStructureConfidenceBreakdown Calculate(
        MapStructureCandidate best,
        double candidateMargin,
        MapStructureRegistrationTuning tuning,
        MapStructureRejectionReason hardGateFailure =
            MapStructureRejectionReason.None,
        bool isTrackingMode = false,
        double sideEntrancePrior = 0d)
    {
        var chamferQuality = Math.Clamp(
            1d - (best.ChamferPixels / tuning.MaximumChamferPixels),
            0d,
            1d);
        var partitionQuality = Math.Clamp(best.ConsistentPartitions / 4d, 0d, 1d);
        var geometricFitQuality = Math.Clamp(
            (chamferQuality * 0.60d)
            + (best.EdgeCoverage * 0.40d),
            0d,
            1d);
        double? featureConsensus = best.FeatureInlierCount > 0
            ? best.FeatureConsensus
            : null;
        double? refinementQuality = best.EccConverged
            ? Math.Clamp(best.EccCorrelation, 0d, 1d)
            : null;
        var boundsAndPrior = best.IsWithinValidBounds
            ? best.PriorAgreement
            : 0d;
        var evidenceItems = new (double? Value, double Weight)[]
        {
            (best.OccupancyCoverage, 0.35d),
            (partitionQuality, 0.25d),
            (featureConsensus, 0.20d),
            (refinementQuality, 0.20d)
        };
        var availableEvidence = evidenceItems
            .Where(item => item.Value is { } value && double.IsFinite(value))
            .ToArray();
        var evidenceWeight = availableEvidence.Sum(item => item.Weight);
        var evidenceConfidence = evidenceWeight <= 0d
            ? 0d
            : Math.Clamp(
                availableEvidence.Sum(item =>
                    Math.Clamp(item.Value!.Value, 0d, 1d) * item.Weight)
                    / evidenceWeight,
                0d,
                1d);
        var geometricLockConfidence = Math.Clamp(
            (geometricFitQuality * 0.75d)
            + (boundsAndPrior * 0.15d)
            + (candidateMargin * 0.10d),
            0d,
            1d);

        // 追踪模式下(已知地图ID)降低chamfer权重，提高覆盖率权重：
        // 视口边缘裁剪、动态遮挡等会降低chamfer分数，但edge/occupancy
        // 覆盖率能更可靠地反映对齐质量。
        var structureQuality = isTrackingMode
            ? Math.Clamp(
                (chamferQuality * 0.15d)        // 35% → 15%
                + (best.EdgeCoverage * 0.35d)   // 30% → 35%
                + (best.OccupancyCoverage * 0.35d) // 20% → 35%
                + (partitionQuality * 0.15d),   // 保持 15%
                0d,
                1d)
            : Math.Clamp(
                (chamferQuality * 0.35d)
                + (best.EdgeCoverage * 0.30d)
                + (best.OccupancyCoverage * 0.20d)
                + (partitionQuality * 0.15d),
                0d,
                1d);

        // ORB voting is a proposal generator. A tiny accidental cluster is
        // not contradictory evidence and must not reduce an otherwise valid
        // geometric lock; only trusted consensus participates in the runtime
        // lock score. The raw value remains in EvidenceConfidence/diagnostics.
        var runtimeFeatureConsensus = featureConsensus.HasValue
            && featureConsensus.Value >= StructureRegistrationRules.MinimumTrustedFeatureConsensus
            ? featureConsensus
            : null;
        var runtimeEvidence = new MapRegistrationConfidenceEvidence
        {
            FeatureConsensus = runtimeFeatureConsensus,
            CandidateSeparation = candidateMargin,
            StructureQuality = structureQuality,
            RefinementQuality = refinementQuality,
            BoundsAndPrior = boundsAndPrior
        };
        var runtimeEffectiveWeight = 0.10d + 0.25d + 0.10d
            + (runtimeFeatureConsensus.HasValue ? 0.15d : 0d)
            + (refinementQuality.HasValue ? 0.10d : 0d);
        var lockConfidence = runtimeEvidence.Calculate();

        // 侧门扫描先验融合：地图ID已知，结构配准只需定位视口
        if (sideEntrancePrior > 0d)
        {
            var locationQuality = structureQuality;
            lockConfidence = MapAlignmentConfidence.ComputeSideEntranceStructureConfidence(
                sideEntrancePrior,
                locationQuality,
                candidateMargin,
                runtimeFeatureConsensus ?? -1d,
                refinementQuality ?? -1d);
        }

        var lowEvidenceReasons = new List<string>();
        if (best.OccupancyCoverage < tuning.MinimumOccupancyCoverage)
            lowEvidenceReasons.Add("OccupancyCoverageBelowMinimum");
        if (best.ConsistentPartitions < tuning.MinimumConsistentPartitions)
            lowEvidenceReasons.Add("InconsistentPartitions");
        if (featureConsensus is { } rawFeatureConsensus
            && rawFeatureConsensus
                < StructureRegistrationRules.MinimumTrustedFeatureConsensus)
        {
            lowEvidenceReasons.Add("WeakFeatureConsensus");
        }
        return new MapStructureConfidenceBreakdown
        {
            ChamferPixels = best.ChamferPixels,
            ChamferQuality = chamferQuality,
            EdgeCoverage = best.EdgeCoverage,
            OccupancyCoverage = best.OccupancyCoverage,
            ConsistentPartitions = best.ConsistentPartitions,
            PartitionQuality = partitionQuality,
            StructureQuality = structureQuality,
            FeatureConsensus = featureConsensus,
            CandidateSeparation = candidateMargin,
            RefinementQuality = refinementQuality,
            BoundsAndPrior = boundsAndPrior,
            GeometricFitQuality = geometricFitQuality,
            EvidenceConfidence = evidenceConfidence,
            GeometricLockConfidence = geometricLockConfidence,
            LockConfidence = lockConfidence,
            LowEvidenceReason = lowEvidenceReasons.Count == 0
                ? null
                : string.Join(",", lowEvidenceReasons),
            HardGateFailure = hardGateFailure == MapStructureRejectionReason.None
                ? null
                : hardGateFailure.ToString(),
            EffectiveWeight = runtimeEffectiveWeight,
            FinalScore = lockConfidence
        };
    }
}
/*
 * 文件职责：MapStructureRegistrationModels.Detail。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
