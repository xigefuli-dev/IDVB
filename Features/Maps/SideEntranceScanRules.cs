using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

public enum SideEntranceFeatureSourceMode
{
    PrebuiltStructureLine
}

/// <summary>
/// 侧门扫描调参。可通过 <see cref="IConfigProvider"/> 在 "side_entrance" TOML
/// 段下覆盖资产生成参数、合法尺度和并行度。扫描网格与预算由
/// ScanExecutionPolicy 在操作开始时冻结，分辨率预设不覆盖用户档位。
/// </summary>
public sealed class SideEntranceScanConfig
{
    /// <summary>是否将侧门特征裁剪中心向内挤压，以保证裁剪框完全位于识别图内。</summary>
    public bool ClampFeatureToBounds { get; set; } = true;
    /// <summary>侧门特征宽度和高度相对识别图宽高的比例。</summary>
    public double FeatureRegionRatio { get; set; } = 0.40d;
    /// <summary>生成侧门特征时首选的数据源模式。</summary>
    public SideEntranceFeatureSourceMode FeatureSourceMode { get; set; } =
        SideEntranceFeatureSourceMode.PrebuiltStructureLine;
    /// <summary>跨地图扫描并行度；1 = 串行。</summary>
    public int ScanParallelism { get; set; } = 4;
    /// <summary>候选窗口最多展示多少条待验证线索。</summary>
    public int MaximumReferenceCandidates { get; set; } = 5;
    /// <summary>允许的最小缩放（识别图 → 实时帧）。</summary>
    public double MinimumScale { get; set; } = 0.55d;
    /// <summary>允许的最大缩放（识别图 → 实时帧）。</summary>
    public double MaximumScale { get; set; } = 5d;
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
                : 0.40d,
            0.01d,
            1d);
    public static SideEntranceFeatureSourceMode FeatureSourceMode =>
        _config.FeatureSourceMode;

    public static int ScanParallelism => _config.ScanParallelism;
    public static int MaximumReferenceCandidates =>
        Math.Max(1, _config.MaximumReferenceCandidates);
    public static double MinimumScale => double.IsFinite(_config.MinimumScale)
        ? Math.Clamp(_config.MinimumScale, .1, 4) : .55;
    public static double MaximumScale => double.IsFinite(_config.MaximumScale)
        ? Math.Clamp(_config.MaximumScale, MinimumScale, 5) : 5;

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
