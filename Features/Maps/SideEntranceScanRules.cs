using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 侧门扫描调参。可通过 <see cref="IConfigProvider"/> 在 "side_entrance" TOML
/// 段下覆盖。三个分辨率预设目录（1920x1080 / 2560x1440 / 2560x1600）各提供
/// 专属 side_entrance.toml，按分辨率定制特征区域和扫描网格密度。
/// </summary>
public sealed class SideEntranceScanConfig
{
    /// <summary>是否将侧门特征裁剪中心向内挤压，以保证裁剪框完全位于识别图内。</summary>
    public bool ClampFeatureToBounds { get; set; } = false;
    /// <summary>侧门特征宽度和高度相对识别图宽高的比例。</summary>
    public double FeatureRegionRatio { get; set; } = 0.12d;
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

    public static bool ClampFeatureToBounds => _config.ClampFeatureToBounds;
    public static double FeatureRegionRatio =>
        Math.Clamp(
            double.IsFinite(_config.FeatureRegionRatio)
                ? _config.FeatureRegionRatio
                : 0.12d,
            0.01d,
            1d);
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
/*
 * 文件职责：SideEntranceScanRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
