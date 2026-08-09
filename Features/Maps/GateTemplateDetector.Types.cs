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
