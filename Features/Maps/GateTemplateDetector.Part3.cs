using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class GateTemplateDetector
{
    public bool HasWarmScale => _warmScale is { } warm && warm > 0d;

    public double? WarmScale => _warmScale;

    public void RememberSuccessfulScale(double scale)
    {
        if (double.IsFinite(scale) && scale > 0d)
            _warmScale = scale;
    }

    public void ResetSuccessfulScale() => _warmScale = null;

    private IReadOnlyList<double> GetScalesForMode(
        GateSearchContext context, double clientWidth)
    {
        return context.Mode switch
        {
            GateSearchMode.WarmScaleSearch => GetWarmOnlyScales(context.WarmScale),
            GateSearchMode.LocalConfirmationSearch =>
                GetConfirmationScales(context.PredictedScale),
            GateSearchMode.LockedScale =>
                GetLockedOnlyScales(context.LockedScale),
            _ => GetFullScales(clientWidth),
        };
    }

    private IReadOnlyList<double> GetFullScales(double clientWidth)
    {
        var normalizedClientWidth = double.IsFinite(clientWidth) && clientWidth > 0d
            ? clientWidth
            : GateTemplateRules.ReferenceClientWidth;
        var estimatedScale = Math.Clamp(
            GateTemplateRules.ReferenceScale * normalizedClientWidth
                / GateTemplateRules.ReferenceClientWidth,
            0.12d,
            1.5d);
        // A remembered successful scale is stronger evidence than either
        // neighbouring warm samples or the client-width estimate.  Search
        // the centre first and expand outwards; otherwise an undersized
        // neighbour can produce two merely adequate matches and trigger the
        // dual-gate early exit before the exact scale is evaluated.
        //
        // Global 0.5…1.5 fallback is intentionally omitted from the default
        // list: those large templates dominate MatchTemplate cost on ~1400px
        // viewports and almost never match real gate icons (~0.15–0.4).
        IEnumerable<double> warmScales = _warmScale is { } warm
            ? new[]
            {
                warm,
                warm * (1d - GateTemplateRules.WarmScaleStep),
                warm * (1d + GateTemplateRules.WarmScaleStep),
                warm * GateTemplateRules.WarmScaleStart,
                warm * GateTemplateRules.WarmScaleMaximum,
            }
            : [];
        // Client-relative band only (no flat 0.5…1.5 global list).  Keep
        // enough samples for cold-start coverage while staying well under the
        // historical ~21-scale tax on large viewports.
        var clientRelativeScales = new[]
            {
                1d,
                GateTemplateRules.WarmScaleStart,
                GateTemplateRules.WarmScaleMaximum,
                0.7d,
                1.35d,
                0.55d,
                1.65d,
                2d,
                2.4d,
                2.8d,
            }
            .Select(factor => estimatedScale * factor);
        return warmScales
            .Concat(clientRelativeScales)
            .Select(scale => Math.Clamp(scale, 0.12d, 1.5d))
            .DistinctBy(scale => Math.Round(scale, 3))
            .ToArray();
    }

    private static bool TrySingleGateEarlyExit(
        GateSearchContext searchContext,
        List<GateDetection> raw,
        int scalesEvaluated,
        out GateSearchStopReason stopReason)
    {
        stopReason = GateSearchStopReason.Completed;
        if (searchContext.Mode == GateSearchMode.FullSearch
            && scalesEvaluated
                < GateTemplateRules.FullSearchMinScalesBeforeSingleGateExit)
        {
            return false;
        }

        var clusters = ClusterAcrossScales(raw);
        if (clusters.Count == 1)
        {
            var best = clusters[0]
                .OrderByDescending(c => c.Score)
                .First();
            if (best.Score < searchContext.SingleGateScoreThreshold)
                return false;

            if (searchContext.WarmScale is { } warmScale)
            {
                if (Math.Abs((best.Scale / warmScale) - 1d)
                    > searchContext.SingleGateScaleTolerance)
                {
                    return false;
                }
            }
            // FullSearch without an explicit warm scale: high single-cluster
            // score after MinScales is enough to stop burning remaining scales.

            stopReason = GateSearchStopReason.SingleGateWarmExit;
            return true;
        }

        if (clusters.Count >= 2
            && searchContext.Mode == GateSearchMode.WarmScaleSearch)
        {
            var ordered = clusters
                .Select(c => c.OrderByDescending(g => g.Score).First())
                .OrderByDescending(c => c.Score)
                .ToArray();
            if (ordered[0].Score - ordered[1].Score
                >= searchContext.AmbiguityScoreGap)
            {
                stopReason = GateSearchStopReason.SingleGateWarmExit;
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<double> GetWarmOnlyScales(double? contextWarmScale)
    {
        // Caller-supplied warm scale (e.g. side-entrance multi-scale scan result)
        // takes priority; fall back to the detector's remembered scale from the
        // last successful detection. Cold-start with an explicit context scale
        // must work — the instance field is null until a detection succeeds.
        var warm = contextWarmScale is { } cw && double.IsFinite(cw) && cw > 0d
            ? cw
            : _warmScale is { } remembered && remembered > 0d
                ? remembered
                : 0d;
        if (warm <= 0d)
            return [];

        return new[]
        {
            warm * GateTemplateRules.WarmScaleStart,
            warm * 0.90d,
            warm * 0.95d,
            warm,
            warm * 1.05d,
            warm * 1.10d,
            warm * GateTemplateRules.WarmScaleMaximum,
        }
        .Select(s => Math.Clamp(s, 0.12d, 1.5d))
        .DistinctBy(s => Math.Round(s, 3))
        .ToArray();
    }
}
