using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>Internal gate-detector rules; overridable via IConfigProvider.</summary>
internal static class GateTemplateRules
{
    // Physical calibration constants — these are reference measurements, not tunables.
    public const double ReferenceClientWidth = 2560d;
    public const double ReferenceScale = 0.275d;

    // Bounded fallback for frames where the configured strict threshold
    // leaves fewer than two gate candidates.
    public const double FallbackPairThreshold = 0.6d;

    private static GateConfig _config = new();

    /// <summary>Template matching threshold used when the caller doesn't specify one.</summary>
    public static double MatchThreshold => _config.MatchThreshold;

    public static double EarlyExitScoreThreshold => _config.EarlyExitScoreThreshold;
    public static double NmsIouThreshold => _config.NmsIouThreshold;
    public static double SpatialClusterIouThreshold => _config.SpatialClusterIouThreshold;
    public static int MaximumGateCandidates => _config.MaximumGateCandidates;
    public static double CannyLowThreshold => _config.CannyLowThreshold;
    public static double CannyHighThreshold => _config.CannyHighThreshold;
    public static double WarmScaleStart => _config.WarmScaleStart;
    public static double WarmScaleStep => _config.WarmScaleStep;
    public static double WarmScaleMaximum => _config.WarmScaleMaximum;
    public static double SingleGateScaleTolerance => _config.SingleGateScaleTolerance;
    public static double SingleGateAmbiguityGap => _config.SingleGateAmbiguityGap;

    /// <summary>
    /// Default time budget for WarmScaleSearch when settings leave the budget at 0.
    /// </summary>
    public static int DefaultWarmGateSearchBudgetMs => _config.DefaultWarmGateSearchBudgetMs;

    /// <summary>
    /// Budgeted FullSearch for selected-map / side-entrance when no warm scale
    /// is available yet. Prevents the historical ~21-scale × full-viewport tax.
    /// </summary>
    public static int DefaultSelectedMapFullSearchBudgetMs => _config.DefaultSelectedMapFullSearchBudgetMs;

    /// <summary>
    /// FullSearch may single-gate early-exit only after this many scales have
    /// been evaluated (avoids stopping on a lucky first undersized match).
    /// </summary>
    public static int FullSearchMinScalesBeforeSingleGateExit => _config.FullSearchMinScalesBeforeSingleGateExit;

    /// <summary>Apply a pre-populated GateConfig instance.</summary>
    internal static void ApplyConfig(GateConfig config)
    {
        _config = config ?? new GateConfig();
    }

    /// <summary>Read and apply configuration from an IConfigProvider under "detection.gate".</summary>
    internal static void ApplyConfig(IConfigProvider provider)
    {
        _config = provider.Get<GateConfig>("gate") ?? new GateConfig();
    }
}
/*
 * 文件职责：GateTemplateRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
