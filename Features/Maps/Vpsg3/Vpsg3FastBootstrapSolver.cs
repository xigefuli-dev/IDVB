using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Top-level production solver for VPSG 3.0 Fast Registration (Bootstrap).
/// Orchestrates Scale estimation, Translation search, Local refinement, and Joint Verification.
/// Emits comprehensive diagnostics and microsecond-level stage timings.
/// Never invokes VPSG2 fallback internally.
/// </summary>
public static class Vpsg3FastBootstrapSolver
{
    /// <summary>
    /// Attempts to solve structural alignment from a live observation against a prepared floor index.
    /// The S-B scale stage is a proposal stage: when it detects a harmonic family (≈2× / ≈0.5× /
    /// ≈0.75× / ≈1.5× …), up to three scale hypotheses are arbitrated through the 2D registration +
    /// verification gate, and the first hypothesis that passes the gate wins. In the clean
    /// single-peak case only the dominant scale is evaluated — the hot path cost is unchanged.
    /// </summary>
    public static Vpsg3BootstrapResult TrySolve(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null,
        double? knownScaleSeed = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3TuningConfig.Default;
        var sc = scratch ?? Vpsg3SolverScratch.Current;

        var swTotal = Stopwatch.StartNew();
        var extractionMs = observation.ExtractionMilliseconds;
        var bounds = observation.ViewportBounds;
        var width = observation.Width;
        var height = observation.Height;

        // Stage 1: Scale Solver (S-B harmonic-aware pitch correlation)
        Vpsg3ScaleResult scaleResult;
        double scaleMs;
        int hypothesisCount;
        double primarySeedScale;

        if (knownScaleSeed is { } seed && seed >= cfg.MinSupportedScale && seed <= cfg.MaxSupportedScale)
        {
            // 稳态路径：当前楼层已知可靠尺度种子，直接复用该先验，彻底省去 12ms 的 1D 直方图投影自相关！
            scaleResult = new Vpsg3ScaleResult(Vpsg3ScaleStatus.Success, seed, PeakRatio: 10.0d, Axis: 0, RejectReason: string.Empty);
            scaleMs = 0d;
            hypothesisCount = 1;
            primarySeedScale = seed;
        }
        else
        {
            var swScale = Stopwatch.StartNew();
            var stage = Vpsg3ScaleSolver.BuildScaleStage(observation, preparedFloor, cfg, sc);
            swScale.Stop();
            scaleMs = swScale.Elapsed.TotalMilliseconds;

            if (!stage.Success)
            {
                swTotal.Stop();
                var timing = new Vpsg3SolverStageTiming(extractionMs, scaleMs, 0d, 0d, 0d, 0d, extractionMs + swTotal.Elapsed.TotalMilliseconds);
                scaleResult = Vpsg3ScaleResult.Failed(stage.Status, stage.RejectReason, stage.PrimaryPeakRatio);
                return Vpsg3BootstrapResult.Fallback($"ScaleSolverFailed: {stage.RejectReason}", scaleResult, timing);
            }

            scaleResult = new Vpsg3ScaleResult(
                Vpsg3ScaleStatus.Success,
                stage.PrimaryScale,
                stage.PrimaryPeakRatio,
                Axis: 0,
                RejectReason: string.Empty);
            hypothesisCount = stage.HypothesisCount;
            primarySeedScale = stage.PrimaryScale;
        }

        var lockScale = knownScaleSeed.HasValue;

        // Stage 2-5: arbitrate each scale hypothesis through translation → refine → spatial → gate.
        // The gate is the decision stage; the scale stage only proposes. Hypotheses are evaluated in
        // correlation order and the *first* hypothesis that passes the gate is adopted immediately:
        // a genuinely wrong harmonic (≈2× / ≈0.75× of the truth) is rejected by the gate with a thin or
        // negative margin (the observed failure signature), so the arbitration proceeds to the next
        // hypothesis only when the current one fails. Healthy single-peak frames pass on hypothesis 0
        // and never pay for an extra full registration pass.
        var totalTransMs = 0d;
        var totalRefineMs = 0d;
        var totalVerMs = 0d;
        var totalGateMs = 0d;

        Vpsg3HypothesisEvaluation primaryEval = default;
        var primaryEvalValid = false;
        var evaluatedCount = 0;
        System.Text.StringBuilder? trace = null;

        for (var i = 0; i < hypothesisCount; i++)
        {
            var seedScale = i == 0
                ? primarySeedScale
                : sc.ScaleHypothesisBuffer[i].SeedScale;
            var hypothesisPeakRatio = i == 0
                ? scaleResult.PeakRatio
                : sc.ScaleHypothesisBuffer[i].PeakRatio;
            var hypothesisScaleResult = new Vpsg3ScaleResult(
                Vpsg3ScaleStatus.Success,
                seedScale,
                hypothesisPeakRatio,
                Axis: 0,
                RejectReason: string.Empty);

            var eval = EvaluateHypothesis(
                observation, preparedFloor, cfg, sc,
                seedScale, lockScale, hypothesisScaleResult,
                bounds, width, height, extractionMs);

            totalTransMs += eval.TranslationMs;
            totalRefineMs += eval.RefineMs;
            totalVerMs += eval.VerificationMs;
            totalGateMs += eval.GateMs;
            evaluatedCount = i + 1;

            if (i == 0)
            {
                primaryEval = eval;
                primaryEvalValid = true;
            }
            else
            {
                // Arbitration actually ran (a secondary hypothesis was evaluated) — lazily build a
                // per-hypothesis trace. Single-hypothesis frames keep `trace == null` and allocate nothing.
                trace ??= new System.Text.StringBuilder();
                AppendEvaluationTrace(trace, 0, primarySeedScale, primaryEval);
                AppendEvaluationTrace(trace, i, seedScale, eval);
            }

            if (!eval.Gate.Passed)
            {
                continue;
            }

            // First passing hypothesis wins the arbitration.
            swTotal.Stop();
            var passTiming = new Vpsg3SolverStageTiming(
                extractionMs, scaleMs, totalTransMs, totalRefineMs, totalVerMs, totalGateMs,
                extractionMs + swTotal.Elapsed.TotalMilliseconds);
            var accepted = new Vpsg3BootstrapResult(
                isAccepted: true,
                fallbackReason: string.Empty,
                scale: eval.Best.Scale,
                offsetX: eval.Best.OffsetX,
                offsetY: eval.Best.OffsetY,
                confidence: eval.Best.WeightedScore,
                apertureMargin: eval.Gate.Margin,
                hasDistinctRunnerUp: eval.Gate.HasDistinctRunnerUp,
                passedPartitions: eval.Best.Spatial.PassedPartitions,
                scaleResult: hypothesisScaleResult,
                bestCandidate: eval.Best,
                runnerUpCandidate: eval.RunnerUp,
                timing: passTiming);
            accepted.EvaluatedHypothesisCount = evaluatedCount;
            accepted.ScaleHypothesisCount = hypothesisCount;
            accepted.ScaleArbitrationSummary = trace?.ToString();
            return accepted;
        }

        // No hypothesis passed the gate. Report the primary (first) evaluation — same semantics as
        // the pre-arbitration solver: the dominant scale's failure is the reason alignment was not accepted.
        swTotal.Stop();
        var fullTiming = new Vpsg3SolverStageTiming(
            extractionMs, scaleMs, totalTransMs, totalRefineMs, totalVerMs, totalGateMs,
            extractionMs + swTotal.Elapsed.TotalMilliseconds);

        var rejected = primaryEvalValid ? primaryEval : default;
        var rejectedResult = new Vpsg3BootstrapResult(
            isAccepted: false,
            fallbackReason: primaryEvalValid && rejected.Gate.FailureReason is { Length: > 0 } reason
                ? reason
                : "AllScaleHypothesesRejected",
            scale: primaryEvalValid ? rejected.Best.Scale : scaleResult.SeedScale,
            offsetX: primaryEvalValid ? rejected.Best.OffsetX : 0d,
            offsetY: primaryEvalValid ? rejected.Best.OffsetY : 0d,
            confidence: primaryEvalValid ? rejected.Best.WeightedScore : 0d,
            apertureMargin: primaryEvalValid ? rejected.Gate.Margin : 0d,
            hasDistinctRunnerUp: primaryEvalValid && rejected.Gate.HasDistinctRunnerUp,
            passedPartitions: primaryEvalValid ? rejected.Best.Spatial.PassedPartitions : 0,
            scaleResult: scaleResult,
            bestCandidate: primaryEvalValid ? rejected.Best : default,
            runnerUpCandidate: primaryEvalValid ? rejected.RunnerUp : null,
            timing: fullTiming);
        rejectedResult.EvaluatedHypothesisCount = evaluatedCount;
        rejectedResult.ScaleHypothesisCount = hypothesisCount;
        rejectedResult.ScaleArbitrationSummary = trace?.ToString();
        return rejectedResult;
    }

    /// <summary>
    /// Appends one hypothesis evaluation to the arbitration trace, e.g.
    /// <c>0:seed=0.8857-&gt;0.8807 g=0.593 m=0.093 pass; 1:seed=0.4429-&gt;… reject(NoDistinct…);</c>.
    /// </summary>
    private static void AppendEvaluationTrace(
        System.Text.StringBuilder trace,
        int index,
        double seedScale,
        Vpsg3HypothesisEvaluation eval)
    {
        var outcome = eval.Gate.Passed
            ? "pass"
            : $"reject({ShortGateReason(eval.Gate.FailureReason)})";
        trace.Append(index)
            .Append(":seed=").Append(seedScale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append("->").Append(eval.Best.Scale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" g=").Append(eval.Best.Spatial.GlobalScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" m=").Append(eval.Gate.Margin.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ').Append(outcome).Append("; ");
    }

    /// <summary>
    /// Trims a gate failure reason to its leading clause (before ':'), e.g.
    /// <c>ApertureMarginBelowThreshold: …</c> → <c>ApertureMarginBelowThreshold</c>.
    /// </summary>
    private static string ShortGateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "rejected";
        var colon = reason.IndexOf(':');
        return colon > 0 ? reason.Substring(0, colon) : reason;
    }

    /// <summary>
    /// Evaluates one scale hypothesis end-to-end: T-3 translation candidate generation, local
    /// refinement of the top candidate and distinct runner-ups, spatial verification, and the joint
    /// verification gate decision. Value-typed result; zero managed allocations on the pass path.
    /// </summary>
    private static Vpsg3HypothesisEvaluation EvaluateHypothesis(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig cfg,
        Vpsg3SolverScratch sc,
        double seedScale,
        bool lockScale,
        Vpsg3ScaleResult scaleResult,
        MapScreenRect bounds,
        int width,
        int height,
        double extractionMs)
    {
        var sparsePoints = observation.SparseEdgePoints;

        // Stage 2: Translation Solver (T-3 Bitset Constellation Correlation)
        var swTrans = Stopwatch.StartNew();
        var (top1Cand, runnerUpCand1, runnerUpCand2, hasDistinctRunnerUp) = Vpsg3TranslationSolver.GenerateCandidates(
            observation, preparedFloor, seedScale, cfg, sc);
        swTrans.Stop();
        var transMs = swTrans.Elapsed.TotalMilliseconds;

        if (top1Cand.RawScore < 5)
        {
            return new Vpsg3HypothesisEvaluation(
                default, null, new Vpsg3GateResult(false, 0d, false, "TranslationNoCandidatesFound"),
                transMs, 0d, 0d, 0d);
        }

        // Stage 3 & 4: Local Refinement & Spatial Verification
        var swRefine = Stopwatch.StartNew();

        // Refine Candidate 1
        var (rfScale1, rfX1, rfY1, rfScore1, probes1) = Vpsg3LocalRefiner.Refine(
            sparsePoints, preparedFloor, seedScale, top1Cand.OffsetX, top1Cand.OffsetY,
            bounds, width, height, lockScale);

        // Refine Distinct Runner-Up 1
        var (rfScale2, rfX2, rfY2, rfScore2, probes2) = hasDistinctRunnerUp && runnerUpCand1.HasValue
            ? Vpsg3LocalRefiner.Refine(sparsePoints, preparedFloor, seedScale, runnerUpCand1.Value.OffsetX, runnerUpCand1.Value.OffsetY, bounds, width, height, lockScale)
            : (seedScale, 0d, 0d, 0d, 0);

        // Refine Distinct Runner-Up 2 if present
        var (rfScale3, rfX3, rfY3, rfScore3, probes3) = runnerUpCand2.HasValue
            ? Vpsg3LocalRefiner.Refine(sparsePoints, preparedFloor, seedScale, runnerUpCand2.Value.OffsetX, runnerUpCand2.Value.OffsetY, bounds, width, height, lockScale)
            : (seedScale, 0d, 0d, 0d, 0);

        swRefine.Stop();
        var refineMs = swRefine.Elapsed.TotalMilliseconds;

        // Spatial Verification
        var swVer = Stopwatch.StartNew();
        var validMask = observation.ValidMask;

        var sp1 = Vpsg3VerificationGate.EvaluateSpatialVerification(
            sparsePoints, validMask, preparedFloor, rfScale1, rfX1, rfY1, bounds, width, height, cfg);

        Vpsg3SpatialResult? sp2 = null;
        if (hasDistinctRunnerUp && runnerUpCand1.HasValue)
        {
            sp2 = Vpsg3VerificationGate.EvaluateSpatialVerification(
                sparsePoints, validMask, preparedFloor, rfScale2, rfX2, rfY2, bounds, width, height, cfg);
        }

        Vpsg3SpatialResult? sp3 = null;
        if (runnerUpCand2.HasValue)
        {
            sp3 = Vpsg3VerificationGate.EvaluateSpatialVerification(
                sparsePoints, validMask, preparedFloor, rfScale3, rfX3, rfY3, bounds, width, height, cfg);
        }

        swVer.Stop();
        var verMs = swVer.Elapsed.TotalMilliseconds;

        var refinedCandidate1 = new Vpsg3RefinedCandidate(
            rfScale1, rfX1, rfY1, rfScore1, sp1.GlobalScore, 0d, sp1, probes1);

        // Refinement can collapse two coarse peaks into the same basin. Only a
        // still-distinct competitor may participate in the aperture comparison.
        if (double.Hypot(rfX2 - rfX1, rfY2 - rfY1) < cfg.MinDistinctDistance) sp2 = null;
        if (double.Hypot(rfX3 - rfX1, rfY3 - rfY1) < cfg.MinDistinctDistance) sp3 = null;

        // Pick the most competitive runner-up (highest refined verification score)
        Vpsg3RefinedCandidate? refinedCandidate2 = null;
        if (sp2.HasValue && sp3.HasValue)
        {
            if (sp3.Value.GlobalScore > sp2.Value.GlobalScore)
            {
                refinedCandidate2 = new Vpsg3RefinedCandidate(rfScale3, rfX3, rfY3, rfScore3, sp3.Value.GlobalScore, 0d, sp3.Value, probes2 + probes3);
            }
            else
            {
                refinedCandidate2 = new Vpsg3RefinedCandidate(rfScale2, rfX2, rfY2, rfScore2, sp2.Value.GlobalScore, 0d, sp2.Value, probes2 + probes3);
            }
        }
        else if (sp2.HasValue)
        {
            refinedCandidate2 = new Vpsg3RefinedCandidate(rfScale2, rfX2, rfY2, rfScore2, sp2.Value.GlobalScore, 0d, sp2.Value, probes2);
        }
        else if (sp3.HasValue)
        {
            refinedCandidate2 = new Vpsg3RefinedCandidate(rfScale3, rfX3, rfY3, rfScore3, sp3.Value.GlobalScore, 0d, sp3.Value, probes3);
        }

        if (refinedCandidate2 is null && sp1.IsSpatiallyConsistent
            && (rfScore1 >= cfg.MinVerificationScore * 0.85d || sp1.GlobalScore >= cfg.MinVerificationScore * 0.85d))
        {
            // Exhaust the remaining retained pool only when the original competitors
            // collapsed. An unrefined low score is not a safe rejection bound.
            for (var i = 1; i < sc.CandidateCount; i++)
            {
                var candidate = sc.CandidateBuffer[i];
                if (SameSeed(candidate, runnerUpCand1) || SameSeed(candidate, runnerUpCand2)) continue;
                var started = Stopwatch.GetTimestamp();
                var refined = Vpsg3LocalRefiner.Refine(sparsePoints, preparedFloor, seedScale,
                    candidate.OffsetX, candidate.OffsetY, bounds, width, height);
                refineMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (double.Hypot(refined.RefinedX - rfX1, refined.RefinedY - rfY1) < cfg.MinDistinctDistance)
                    continue;
                started = Stopwatch.GetTimestamp();
                var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(sparsePoints, validMask,
                    preparedFloor, refined.RefinedScale, refined.RefinedX, refined.RefinedY, bounds, width, height, cfg);
                verMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (refinedCandidate2 is null || spatial.GlobalScore > refinedCandidate2.Value.Spatial.GlobalScore)
                    refinedCandidate2 = new Vpsg3RefinedCandidate(refined.RefinedScale, refined.RefinedX,
                        refined.RefinedY, refined.BestScore, spatial.GlobalScore, 0, spatial, refined.Probes);
            }
        }

        // Post-refinement global rank correction: if the runner-up clearly outscores a weak primary
        // candidate after spatial verification, swap them so the true best result enters the gate.
        // The strict thresholds (runnerUp >= 0.80, top1 < 0.70) are intentional: a marginal inversion
        // (both candidates in the 0.70–0.85 band) reflects genuine aperture ambiguity and must remain
        // a fallback. Only promote when the coarse translation solver's top1 is demonstrably poor while
        // the runner-up is unambiguously strong — i.e. margin = score1 - score2 was deeply negative.
        if (refinedCandidate2.HasValue
            && refinedCandidate2.Value.Spatial.GlobalScore >= 0.80d
            && refinedCandidate1.Spatial.GlobalScore < 0.70d)
        {
            (refinedCandidate1, refinedCandidate2) = (refinedCandidate2.Value, refinedCandidate1);
        }

        // Stage 5: Joint Verification Gate Decision
        var swGate = Stopwatch.StartNew();
        var gateDecision = Vpsg3VerificationGate.EvaluateDecision(
            scaleResult,
            refinedCandidate1,
            refinedCandidate2,
            refinedCandidate2.HasValue,
            bounds,
            preparedFloor.ReferenceWidth,
            preparedFloor.ReferenceHeight,
            cfg);
        swGate.Stop();
        var gateMs = swGate.Elapsed.TotalMilliseconds;

        return new Vpsg3HypothesisEvaluation(
            refinedCandidate1,
            refinedCandidate2,
            gateDecision,
            transMs,
            refineMs,
            verMs,
            gateMs);
    }

    private static bool SameSeed(Vpsg3TranslationCandidate candidate, Vpsg3TranslationCandidate? other) =>
        other.HasValue && candidate.OffsetX == other.Value.OffsetX && candidate.OffsetY == other.Value.OffsetY;

    /// <summary>
    /// Value-typed outcome of evaluating one scale hypothesis (used by multi-scale arbitration).
    /// </summary>
    private readonly record struct Vpsg3HypothesisEvaluation(
        Vpsg3RefinedCandidate Best,
        Vpsg3RefinedCandidate? RunnerUp,
        Vpsg3GateResult Gate,
        double TranslationMs,
        double RefineMs,
        double VerificationMs,
        double GateMs);
}
