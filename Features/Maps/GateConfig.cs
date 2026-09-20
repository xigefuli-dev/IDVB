namespace IDVBuff.Features.Maps;

/// <summary>
/// Gate detection algorithm parameters that can be overridden via
/// <see cref="IDVBuff.Core.Contracts.IConfigProvider"/> under the
/// "detection.gate" TOML section.
/// </summary>
internal sealed class GateConfig
{
    // ── Template matching ───────────────────────────────────────────
    public double MatchThreshold { get; set; } = 0.72d;

    // ── NMS / spatial clustering ────────────────────────────────────
    public double NmsIouThreshold { get; set; } = 0.25d;
    public double SpatialClusterIouThreshold { get; set; } = 0.35d;
    public int MaximumGateCandidates { get; set; } = 6;

    // ── Scale search band ───────────────────────────────────────────
    public double WarmScaleStart { get; set; } = 0.85d;
    public double WarmScaleStep { get; set; } = 0.075d;
    public double WarmScaleMaximum { get; set; } = 1.15d;

    // ── Early exit ──────────────────────────────────────────────────
    public double EarlyExitScoreThreshold { get; set; } = 0.85d;
    public double SingleGateScaleTolerance { get; set; } = 0.15d;
    public double SingleGateAmbiguityGap { get; set; } = 0.08d;
    public int FullSearchMinScalesBeforeSingleGateExit { get; set; } = 5;

    // ── Edge detection (Canny) ─────────────────────────────────────
    public double CannyLowThreshold { get; set; } = 45d;
    public double CannyHighThreshold { get; set; } = 135d;

    // ── Time budgets ────────────────────────────────────────────────
    public int DefaultWarmGateSearchBudgetMs { get; set; } = 120;
    public int DefaultSelectedMapFullSearchBudgetMs { get; set; } = 150;
}
/*
 * 文件职责：GateConfig。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
