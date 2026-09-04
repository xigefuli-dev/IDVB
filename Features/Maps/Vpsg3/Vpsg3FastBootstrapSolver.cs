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
    /// </summary>
    public static Vpsg3BootstrapResult TrySolve(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3TuningConfig.Default;
        var sc = scratch ?? Vpsg3SolverScratch.Current;

        var swTotal = Stopwatch.StartNew();
        var extractionMs = observation.ExtractionMilliseconds;

        // Stage 1: Scale Solver (S-B Dominant Pitch Correlation)
        var swScale = Stopwatch.StartNew();
        var scaleResult = Vpsg3ScaleSolver.Solve(observation, preparedFloor, cfg, sc);
        swScale.Stop();
        var scaleMs = swScale.Elapsed.TotalMilliseconds;

        if (!scaleResult.Success)
        {
            swTotal.Stop();
            var timing = new Vpsg3SolverStageTiming(extractionMs, scaleMs, 0d, 0d, 0d, 0d, extractionMs + swTotal.Elapsed.TotalMilliseconds);
            return Vpsg3BootstrapResult.Fallback($"ScaleSolverFailed: {scaleResult.RejectReason}", scaleResult, timing);
        }

        var estimatedScale = scaleResult.SeedScale;

        // Stage 2: Translation Solver (T-3 Bitset Constellation Correlation)
        var swTrans = Stopwatch.StartNew();
        var (top1Cand, runnerUpCand1, runnerUpCand2, hasDistinctRunnerUp) = Vpsg3TranslationSolver.GenerateCandidates(
            observation, preparedFloor, estimatedScale, cfg, sc);
        swTrans.Stop();
        var transMs = swTrans.Elapsed.TotalMilliseconds;

        if (top1Cand.RawScore < 5)
        {
            swTotal.Stop();
            var timing = new Vpsg3SolverStageTiming(extractionMs, scaleMs, transMs, 0d, 0d, 0d, extractionMs + swTotal.Elapsed.TotalMilliseconds);
            return Vpsg3BootstrapResult.Fallback("TranslationNoCandidatesFound", scaleResult, timing);
        }

        // Stage 3 & 4: Local Refinement & Spatial Verification
        var swRefine = Stopwatch.StartNew();
        var sparsePoints = observation.SparseEdgePoints;
        var bounds = observation.ViewportBounds;
        var width = observation.Width;
        var height = observation.Height;

        // Refine Candidate 1
        var (rfScale1, rfX1, rfY1, rfScore1, probes1) = Vpsg3LocalRefiner.Refine(
            sparsePoints, preparedFloor, estimatedScale, top1Cand.OffsetX, top1Cand.OffsetY,
            bounds, width, height);

        // Refine Distinct Runner-Up 1
        var (rfScale2, rfX2, rfY2, rfScore2, probes2) = hasDistinctRunnerUp && runnerUpCand1.HasValue
            ? Vpsg3LocalRefiner.Refine(sparsePoints, preparedFloor, estimatedScale, runnerUpCand1.Value.OffsetX, runnerUpCand1.Value.OffsetY, bounds, width, height)
            : (estimatedScale, 0d, 0d, 0d, 0);

        // Refine Distinct Runner-Up 2 if present
        var (rfScale3, rfX3, rfY3, rfScore3, probes3) = runnerUpCand2.HasValue
            ? Vpsg3LocalRefiner.Refine(sparsePoints, preparedFloor, estimatedScale, runnerUpCand2.Value.OffsetX, runnerUpCand2.Value.OffsetY, bounds, width, height)
            : (estimatedScale, 0d, 0d, 0d, 0);

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

        // Stage 5: Joint Verification Gate Decision
        var swGate = Stopwatch.StartNew();
        var gateDecision = Vpsg3VerificationGate.EvaluateDecision(
            scaleResult,
            refinedCandidate1,
            refinedCandidate2,
            hasDistinctRunnerUp,
            bounds,
            preparedFloor.ReferenceWidth,
            preparedFloor.ReferenceHeight,
            cfg);
        swGate.Stop();
        var gateMs = swGate.Elapsed.TotalMilliseconds;

        swTotal.Stop();
        var fullTiming = new Vpsg3SolverStageTiming(
            extractionMs, scaleMs, transMs, refineMs, verMs, gateMs, extractionMs + swTotal.Elapsed.TotalMilliseconds);

        if (!gateDecision.Passed)
        {
            return new Vpsg3BootstrapResult(
                isAccepted: false,
                fallbackReason: gateDecision.FailureReason,
                scale: rfScale1,
                offsetX: rfX1,
                offsetY: rfY1,
                confidence: rfScore1,
                apertureMargin: gateDecision.Margin,
                hasDistinctRunnerUp: gateDecision.HasDistinctRunnerUp,
                passedPartitions: sp1.PassedPartitions,
                scaleResult: scaleResult,
                bestCandidate: refinedCandidate1,
                runnerUpCandidate: refinedCandidate2,
                timing: fullTiming);
        }

        return new Vpsg3BootstrapResult(
            isAccepted: true,
            fallbackReason: string.Empty,
            scale: rfScale1,
            offsetX: rfX1,
            offsetY: rfY1,
            confidence: rfScore1,
            apertureMargin: gateDecision.Margin,
            hasDistinctRunnerUp: gateDecision.HasDistinctRunnerUp,
            passedPartitions: sp1.PassedPartitions,
            scaleResult: scaleResult,
            bestCandidate: refinedCandidate1,
            runnerUpCandidate: refinedCandidate2,
            timing: fullTiming);
    }
}


