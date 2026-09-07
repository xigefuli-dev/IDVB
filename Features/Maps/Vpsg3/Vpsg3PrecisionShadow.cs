using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed record Vpsg3PrecisionShadowResult(
    Vpsg3PrecisionResult? Best, Vpsg3PrecisionResult? RunnerUp,
    bool CandidatePairPassed, string Termination, double TotalMilliseconds);

internal static class Vpsg3PrecisionShadow
{
    // Phase 1 intentionally has no production switch: replay-only until real ground truth passes.
    internal static Vpsg3PrecisionShadowResult Evaluate(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, Vpsg3BootstrapResult baseline, Vpsg3TuningConfig? config = null)
    {
        var budget = Vpsg3PrecisionBudget.Start();
        var cfg = config ?? Vpsg3TuningConfig.Default;
        if (baseline.BestCandidate.Scale <= 0 || !baseline.RunnerUpCandidate.HasValue)
            return new(null, null, false, "baseline-candidate-pair-unavailable", budget.Elapsed);
        var best = Refine(baseline.BestCandidate);
        if (budget.Expired) return new(best, null, false, "budget", budget.Elapsed);
        if (!best.Calibrated) return new(best, null, false, best.Termination, budget.Elapsed);

        var second = budget.Elapsed < 7.5d ? Refine(baseline.RunnerUpCandidate.Value) : null;
        if (budget.Expired) return new(best, second, false, "budget", budget.Elapsed);

        var c1 = Verify(best);
        if (budget.Expired) return new(best, second, false, "budget", budget.Elapsed);

        var c2 = (second is { Calibrated: true }
            && double.Hypot(second.OffsetX - best.OffsetX, second.OffsetY - best.OffsetY) >= cfg.MinDistinctDistance)
            ? Verify(second)
            : baseline.RunnerUpCandidate.Value;
        if (budget.Expired) return new(best, second, false, "budget", budget.Elapsed);

        var gate = Vpsg3VerificationGate.EvaluateDecision(baseline.ScaleResult, c1, c2, true,
            observation.ViewportBounds, floor.ReferenceWidth, floor.ReferenceHeight, cfg);
        var expired = budget.Expired;
        // This describes only the baseline's final pair, NOT a new all-candidate acceptance decision.
        return new(best, second, !expired && gate.Passed,
            expired ? "budget" : gate.Passed ? "candidate-pair-passed" : gate.FailureReason, budget.Elapsed);

        Vpsg3PrecisionResult Refine(Vpsg3RefinedCandidate candidate) =>
            Vpsg3PrecisionRefiner.Refine(observation, floor, candidate.Scale,
                candidate.OffsetX, candidate.OffsetY, budget);

        Vpsg3RefinedCandidate Verify(Vpsg3PrecisionResult result)
        {
            var score = Vpsg3LocalRefiner.EvaluateScore(observation.SparseEdgePoints, floor,
                result.Scale, result.OffsetX, result.OffsetY, observation.ViewportBounds);
            var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(observation.SparseEdgePoints,
                observation.ValidMask, floor, result.Scale, result.OffsetX, result.OffsetY,
                observation.ViewportBounds, observation.Width, observation.Height, cfg);
            return new(result.Scale, result.OffsetX, result.OffsetY, score, spatial.GlobalScore,
                0d, spatial, result.Probes);
        }
    }
}
