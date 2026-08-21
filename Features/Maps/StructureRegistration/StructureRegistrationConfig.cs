namespace IDVBuff.Features.Maps;

/// <summary>
/// 结构配准核心参数，对应 TOML 路径 "alignment.structure"。
/// 包含基本容差、评估公式权重、ORB 特征匹配、验证阈值和局部精修参数。
/// </summary>
internal sealed class StructureConfig
{
    // ── 基本容差 ─────────────────────────────────────────────────
    public double ScaleDiffTolerance { get; set; } = 0.0001d;
    public double RotationTolerance { get; set; } = 0.1d;
    public double MinimumUsableScale { get; set; } = 0.05d;
    public double ScaleAgreementTolerance { get; set; } = 0.003d;
    /// <summary>最终验收时允许候选 scale 相对锁定 scale 的最大偏离比例。
    /// 实际生效门限为 max(本值, 本次搜索半径 scaleSearchRadius)：门限永远
    /// 不严于搜索本身探索过的范围（无门楼层恢复允许 ±0.30）。</summary>
    public double MaximumScaleChangeRatio { get; set; } = 0.15d;
    public double ScaleDuplicateTolerance { get; set; } = 0.000001d;
    public double SpatialDuplicateTolerance { get; set; } = 2d;
    public double CandidateDuplicateRadius { get; set; } = 1d;

    // ── 历史候选 ─────────────────────────────────────────────────
    public int MaxHistoryCandidates { get; set; } = 5;

    // ── 评估公式权重（CompositeCost） ───────────────────────────
    public int MinEdgesPerPartition { get; set; } = 12;
    public double MinPartitionCoverage { get; set; } = 0.45d;
    public double EdgeCoverageWeight { get; set; } = 4d;
    public double OccupancyCoverageWeight { get; set; } = 2d;
    public double BoundsPenalty { get; set; } = 100d;
    public double PartitionPenaltyWeight { get; set; } = 0.75d;
    public double PriorDisagreementPenaltyWeight { get; set; } = 0.75d;

    // ── ORB 特征匹配 ────────────────────────────────────────────
    public int FeatureMinVotes { get; set; } = 3;
    public double FeatureMinInlierTolerance { get; set; } = 2d;
    public double FeatureConsensusCostReduction { get; set; } = 0.5d;
    public double FeatureWeightEpsilon { get; set; } = 0.0001d;
    public int FeatureVoteCountDivisor { get; set; } = 3;

    // ── 验证阈值 ────────────────────────────────────────────────
    public double StrictChamferFactor { get; set; } = 0.90d;
    public double StrictEdgeCoverageMargin { get; set; } = 0.07d;
    public double StrictOccupancyMargin { get; set; } = 0.08d;
    public double MinimumPriorAgreement { get; set; } = 0.05d;
    public double StrictPriorAgreement { get; set; } = 0.20d;
    public double MinimumReplacementMargin { get; set; } = 0.10d;
    public double MinimumTrustedFeatureConsensus { get; set; } = 0.50d;
    public int IsStrongCandidateMinPartitions { get; set; } = 3;
    public int CanSkipRefinementMinPartitions { get; set; } = 3;
    public int EarlyTermMinPartitions { get; set; } = 4;
    public int EarlyTermExtraPartitions { get; set; } = 1;
    public double EarlyTermMarginFactor { get; set; } = 1.5d;
    public double GlobalSearchMarginMultiplier { get; set; } = 1.25d;
    public double MarginNormalizationFloor { get; set; } = 0.01d;
    public double FastMinimumGeometricLockConfidence { get; set; } = 0.60d;

    // ── 局部精修（非 ECC 部分） ─────────────────────────────────
    public int[] RefinementSteps { get; set; } = { 8, 4, 2, 1 };
    public double RefinementEarlyExitScore { get; set; } = 0.001d;
    public double RefinementWorsenTolerance { get; set; } = 0.001d;
    public double RefinementChamferFactor { get; set; } = 0.85d;
    public double RefinementEdgeCoverageMargin { get; set; } = 0.10d;
    public double RefinementOccupancyMargin { get; set; } = 0.10d;
}

/// <summary>
/// ECC 精修参数，对应 TOML 路径 "alignment.ecc"。
/// </summary>
internal sealed class EccConfig
{
    public int MaxIterations { get; set; } = 30;
    public double Epsilon { get; set; } = 0.0001d;
    public int GaussFocalLen { get; set; } = 3;
    public double MinCorrelation { get; set; } = 0.60d;
    public double MaxTranslationShift { get; set; } = 2.5d;
}

/// <summary>
/// 粗搜索 / 金字塔 / 降采样全局搜索参数，对应 TOML 路径 "alignment.coarse"。
/// </summary>
internal sealed class CoarseConfig
{
    // ── 降采样全局搜索 ──────────────────────────────────────────
    public int DownsampleFactor { get; set; } = 2;
    public int MinRefDimension { get; set; } = 200;
    public int MinTplDimension { get; set; } = 40;
    public int FastCoarseMinTemplateDim { get; set; } = 12;

    // ── 金字塔搜索 ──────────────────────────────────────────────
    public int PyramidMinLevels { get; set; } = 3;
    public int PyramidDownsampleFactor { get; set; } = 4;

    // ── NMS 抑制 ────────────────────────────────────────────────
    public int SuppressionDivisor { get; set; } = 3;
    public int CollectCandidatesSuppressionDivisor { get; set; } = 3;
}
/*
 * 文件职责：StructureRegistrationConfig。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
