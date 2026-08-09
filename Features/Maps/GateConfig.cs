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
