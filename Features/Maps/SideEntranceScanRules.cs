using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 侧门扫描调参。可通过 <see cref="IConfigProvider"/> 在 "side_entrance" TOML
/// 段下覆盖。三个分辨率预设目录（1920x1080 / 2560x1440 / 2560x1600）各提供
/// 专属 side_entrance.toml，按分辨率定制扫描网格密度与并行度。
/// </summary>
public sealed class SideEntranceScanConfig
{
    /// <summary>粗搜索的相对步长；决定缩放网格疏密。0.06 → 约 24 档。</summary>
    public double CoarseScaleStep { get; set; } = 0.06d;
    /// <summary>细化阶段在粗峰值两侧各取的档数。</summary>
    public int RefineStepsPerSide { get; set; } = 3;
    /// <summary>粗搜索的降采样倍率（帧尺寸 ÷ 该值）。</summary>
    public int CoarsePyramidFactor { get; set; } = 4;
    /// <summary>旧配置兼容字段；准确优先扫描不再按粗排名截断精化集合。</summary>
    public int RefineCandidateTopK { get; set; } = 5;
    /// <summary>粗分绝对下限，低于该值的地图直接淘汰；0 = 不启用绝对剪枝。</summary>
    public double CoarseScorePruneThreshold { get; set; } = 0d;
    /// <summary>跨地图扫描并行度；1 = 串行。</summary>
    public int ScanParallelism { get; set; } = 4;
    /// <summary>低于此相似度的结果只写诊断，不得展示为候选或参考线索。</summary>
    public double MinimumReferenceSimilarity { get; set; } = 0.55d;
    /// <summary>进入结构复核前所需的最低模板相似度。</summary>
    public double MinimumVerificationSimilarity { get; set; } = 0.68d;
    /// <summary>模板匹配第一名相对第二名的最低分离度。</summary>
    public double MinimumTemplateMargin { get; set; } = 0.035d;
    /// <summary>模板推导的门中心与实际检测门中心允许的最大误差。</summary>
    public double MaximumGateSpatialResidualPixels { get; set; } = 42d;
    /// <summary>落在缩放搜索上下边界附近的结果不能直接成为可靠候选。</summary>
    public double ScaleBoundaryTolerance { get; set; } = 0.02d;
    /// <summary>最多对多少条高质量检索线索运行高成本结构复核。</summary>
    public int MaximumStructureVerificationCandidates { get; set; } = 8;
    /// <summary>候选窗口最多展示多少条待验证线索。</summary>
    public int MaximumReferenceCandidates { get; set; } = 5;
    /// <summary>允许的最小缩放（识别图 → 实时帧）。</summary>
    public double MinimumScale { get; set; } = 0.55d;
    /// <summary>允许的最大缩放（识别图 → 实时帧）。</summary>
    public double MaximumScale { get; set; } = 2.2d;
}

/// <summary>
/// 侧门扫描的可配置规则入口。遵循与 <see cref="RecognitionConfigRules"/> 相同的
/// 模式：内部持有 <see cref="SideEntranceScanConfig"/> 实例，对外暴露 static
/// 属性并通过 ApplyConfig 注入。
/// </summary>
internal static class SideEntranceScanRules
{
    private static SideEntranceScanConfig _config = new();

    public static double CoarseScaleStep => _config.CoarseScaleStep;
    public static int RefineStepsPerSide => _config.RefineStepsPerSide;
    public static int CoarsePyramidFactor => _config.CoarsePyramidFactor;
    public static int RefineCandidateTopK => _config.RefineCandidateTopK;
    public static double CoarseScorePruneThreshold => _config.CoarseScorePruneThreshold;
    public static int ScanParallelism => _config.ScanParallelism;
    public static double MinimumReferenceSimilarity =>
        Math.Clamp(_config.MinimumReferenceSimilarity, 0d, 1d);
    public static double MinimumVerificationSimilarity =>
        Math.Clamp(_config.MinimumVerificationSimilarity, 0d, 1d);
    public static double MinimumTemplateMargin =>
        Math.Clamp(_config.MinimumTemplateMargin, 0d, 1d);
    public static double MaximumGateSpatialResidualPixels =>
        Math.Max(1d, _config.MaximumGateSpatialResidualPixels);
    public static double ScaleBoundaryTolerance =>
        Math.Clamp(_config.ScaleBoundaryTolerance, 0d, 0.25d);
    public static int MaximumStructureVerificationCandidates =>
        Math.Max(1, _config.MaximumStructureVerificationCandidates);
    public static int MaximumReferenceCandidates =>
        Math.Max(1, _config.MaximumReferenceCandidates);
    public static double MinimumScale => _config.MinimumScale;
    public static double MaximumScale => _config.MaximumScale;

    /// <summary>Apply a pre-populated <see cref="SideEntranceScanConfig"/> instance.</summary>
    internal static void ApplyConfig(SideEntranceScanConfig config)
    {
        _config = config ?? new SideEntranceScanConfig();
    }

    /// <summary>
    /// Read and apply configuration from an <see cref="IConfigProvider"/> under
    /// the "side_entrance" TOML section.
    /// </summary>
    internal static void ApplyConfig(IConfigProvider provider)
    {
        _config = provider.Get<SideEntranceScanConfig>("side_entrance")
            ?? new SideEntranceScanConfig();
    }
}
