// IDVB Remaster — GateTemplateDetector 类型定义

using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Controls how GateTemplateDetector enumerates scales and image regions.</summary>
public enum GateSearchMode
{
    /// <summary>Cold-start: warm scales + client-relative scales + global fallback.</summary>
    FullSearch,

    /// <summary>Only a narrow band around the remembered warm scale (~7 scales).</summary>
    WarmScaleSearch,

    /// <summary>Only search small ROIs around predicted gate positions (~3 scales).</summary>
    LocalConfirmationSearch,

    /// <summary>Single fixed scale from a locked gate-pair session — no scale search.</summary>
    LockedScale,
}

/// <summary>Why the search stopped — carried in GateDetectionResult for diagnostics.</summary>
public enum GateSearchStopReason
{
    Completed,
    DualGateEarlyExit,
    SingleGateWarmExit,
    BudgetExceeded,
    NoValidScale,
    InvalidSearchContext,
}

/// <summary>
/// Pure template-detection parameters. Contains no map / geometry / session types.
/// </summary>
public sealed class GateSearchContext
{
    public GateSearchMode Mode { get; init; } = GateSearchMode.FullSearch;

    // ── WarmScaleSearch ──────────────────────────────────────────
    public double? WarmScale { get; init; }

    // ── LocalConfirmationSearch ───────────────────────────────────
    public IReadOnlyList<MapScreenRect> PredictedGateRegions { get; init; } = [];
    public double? PredictedScale { get; init; }

    // ── LockedScale ──────────────────────────────────────────────
    public double? LockedScale { get; init; }

    // ── ROI ──────────────────────────────────────────────────────
    /// <summary>Multiplied by template size and added around each predicted region.</summary>
    public double LocalRoiTemplatePaddingFactor { get; init; } = 1.0;
    public int LocalRoiMinimumPaddingPixels { get; init; } = 24;
    public int MaximumExpectedMotionPixels { get; init; }

    // ── Budget ───────────────────────────────────────────────────
    /// <summary>Checked before each MatchTemplate call. Null = no budget.</summary>
    public int? TimeBudgetMilliseconds { get; set; }

    // ── Single-gate warm exit ────────────────────────────────────
    /// <summary>
    /// Allows the detector to stop after two strong, non-overlapping glyphs
    /// appear at one scale. Initial identity scans disable this so an early
    /// false pair cannot hide gates that occur later in the scale schedule.
    /// </summary>
    public bool AllowDualGateEarlyExit { get; init; } = true;
    public bool AllowSingleGateEarlyExit { get; init; }
    public double SingleGateScoreThreshold { get; init; } =
        GateTemplateRules.EarlyExitScoreThreshold;
    public double SingleGateScaleTolerance { get; init; } =
        GateTemplateRules.SingleGateScaleTolerance;
    public double AmbiguityScoreGap { get; init; } =
        GateTemplateRules.SingleGateAmbiguityGap;
}

/// <summary>Full diagnostic result from one gate detection call.</summary>
public sealed class GateDetectionResult
{
    public IReadOnlyList<GateDetection> Gates { get; init; } = [];
    public IReadOnlyList<GateDetection> RawCandidates { get; init; } = [];
    public GateSearchMode SearchModeUsed { get; init; }
    public GateSearchStopReason StopReason { get; init; }
    public int ScalesEvaluated { get; init; }
    public int RegionsEvaluated { get; init; }
    public int MatchTemplateCalls { get; init; }
    public bool BudgetExceeded { get; init; }
    public double ElapsedMilliseconds { get; init; }
}
/*
 * 文件职责：GateTemplateDetector.Types。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
