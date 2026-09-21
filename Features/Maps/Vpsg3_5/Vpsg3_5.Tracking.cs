using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Configuration parameters for VPSG 3.5 continuous tracking.
/// </summary>
public sealed class Vpsg3_5TrackingConfig
{
    public static Vpsg3_5TrackingConfig Default { get; } = new();

    /// <summary>Local search radius in screen pixels around prior transform position.</summary>
    public int LocalSearchRadius { get; init; } = 36;

    /// <summary>Grid search stride in reference pixels for local window.</summary>
    public int CoarseStride { get; init; } = 2;

    /// <summary>Minimum bitset hit score ratio required to accept the local fast-path candidate.</summary>
    public double MinLocalScoreThreshold { get; init; } = 0.35d;

    /// <summary>Minimum raw bitset hit count required to accept the local fast-path candidate.</summary>
    public int MinLocalHits { get; init; } = 10;

    /// <summary>Maximum number of sparse edge points to sample for tracking.</summary>
    public int MaxSparsePoints { get; init; } = 120;

    /// <summary>Minimum spatial verification weighted score to confirm lock.</summary>
    public double MinVerificationScore { get; init; } = 0.55d;

    /// <summary>Deadband threshold in screen pixels below which transform translation is unchanged.</summary>
    public double DeadbandPixels { get; init; } = 0.5d;

    /// <summary>Whether to run sub-pixel precision distance-field refinement.</summary>
    public bool EnablePrecisionRefinement { get; init; } = true;

    /// <summary>Whether to enable zero-latency mouse feedforward during map dragging.</summary>
    public bool EnableMouseFeedforward { get; init; } = true;

    /// <summary>Scale factor for mouse physical delta to screen pixels (default 1.0).</summary>
    public double MouseScaleRatio { get; init; } = 1.0d;

    /// <summary>Interval in milliseconds between VPSG absolute corrections.</summary>
    public int VisualVerificationIntervalMs { get; init; } = 150;

    /// <summary>Maximum wait for a distinct WGC frame before the GDI fallback is attempted.</summary>
    public int FrameCaptureWaitMs { get; init; } = 50;

    /// <summary>Residual threshold in screen pixels to detect boundary collision.</summary>
    public double BoundaryCollisionThresholdPixels { get; init; } = 12.0d;

    /// <summary>Exponential moving average rate to absorb small drift at low speeds.</summary>
    public double ResidualCorrectionAlpha { get; init; } = 0.2d;

    /// <summary>Maximum allowed translation jump distance in screen pixels between consecutive tracking frames (default 120px).</summary>
    public double MaxTranslationJumpPixels { get; init; } = 120.0d;
}

/// <summary>
/// Microsecond-level stage timings for VPSG 3.5 continuous tracking.
/// </summary>
public readonly record struct Vpsg3_5TrackingTiming(
    double LocalSearchMs,
    double FallbackSearchMs,
    double LocalRefineMs,
    double VerificationMs,
    double PrecisionMs,
    double TotalMs);

/// <summary>
/// Result of a VPSG 3.5 continuous tracking step.
/// </summary>
public sealed class Vpsg3_5TrackingResult
{
    public bool IsAccepted { get; init; }
    public double Scale { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double Confidence { get; init; }
    public double Score { get; init; }
    public int PassedPartitions { get; init; }
    public bool IsLocalFastPath { get; init; }
    public string FallbackReason { get; init; } = string.Empty;
    public Vpsg3_5TrackingTiming Timing { get; init; }

    public static Vpsg3_5TrackingResult Accept(
        double scale,
        double offsetX,
        double offsetY,
        double confidence,
        double score,
        int passedPartitions,
        bool isLocalFastPath,
        Vpsg3_5TrackingTiming timing) => new()
    {
        IsAccepted = true,
        Scale = scale,
        OffsetX = offsetX,
        OffsetY = offsetY,
        Confidence = confidence,
        Score = score,
        PassedPartitions = passedPartitions,
        IsLocalFastPath = isLocalFastPath,
        Timing = timing
    };

    public static Vpsg3_5TrackingResult Reject(
        string reason,
        double scale,
        double priorTx,
        double priorTy,
        Vpsg3_5TrackingTiming timing) => new()
    {
        IsAccepted = false,
        Scale = scale,
        OffsetX = priorTx,
        OffsetY = priorTy,
        Confidence = 0d,
        Score = 0d,
        PassedPartitions = 0,
        IsLocalFastPath = false,
        FallbackReason = reason,
        Timing = timing
    };
}

/// <summary>
/// VPSG 3.5 Continuous Tracking Solver.
/// Specializes in 2-DoF fixed-scale structural translation tracking.
/// Bypasses scale solving entirely and utilizes resident bitset local windows.
/// </summary>
public static class Vpsg3_5TrackingSolver
{
    /// <summary>
    /// Solves continuous tracking displacement for the current frame given a locked scale and prior transform.
    /// </summary>
    public static Vpsg3_5TrackingResult TryTrack(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        double lockedScale,
        double priorTx,
        double priorTy,
        Vpsg3_5TrackingConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3_5TrackingConfig.Default;
        var sc = scratch ?? Vpsg3SolverScratch.Current;
        var swTotal = Stopwatch.StartNew();

        if (preparedFloor.IsDisposed || !double.IsFinite(lockedScale) || lockedScale <= 0.01d)
        {
            swTotal.Stop();
            var emptyTiming = new Vpsg3_5TrackingTiming(0, 0, 0, 0, 0, swTotal.Elapsed.TotalMilliseconds);
            return Vpsg3_5TrackingResult.Reject("InvalidFloorOrScale", lockedScale, priorTx, priorTy, emptyTiming);
        }

        var sparsePoints = observation.SparseEdgePoints;
        var pointCount = sparsePoints.Count;
        if (pointCount == 0)
        {
            swTotal.Stop();
            var emptyTiming = new Vpsg3_5TrackingTiming(0, 0, 0, 0, 0, swTotal.Elapsed.TotalMilliseconds);
            return Vpsg3_5TrackingResult.Reject("NoSparseEdgePoints", lockedScale, priorTx, priorTy, emptyTiming);
        }

        var bounds = observation.ViewportBounds;
        var width = observation.Width;
        var height = observation.Height;
        var invScale = 1.0d / lockedScale;

        // Stage 1: Scale sparse query points to reference coordinate space (Zero Heap Allocation)
        var limitPts = Math.Min(pointCount, Math.Min(cfg.MaxSparsePoints, sc.ScaledQueryPointsBuffer.Length));
        var scaledPts = sc.ScaledQueryPointsBuffer.AsSpan(0, limitPts);
        for (var i = 0; i < limitPts; i++)
        {
            var p = sparsePoints[i];
            scaledPts[i] = new Point(
                (int)Math.Round(p.X * invScale),
                (int)Math.Round(p.Y * invScale));
        }

        // Stage 2: Local Prior Window Bitset Search (Fast Path)
        var swLocal = Stopwatch.StartNew();
        var (localCandidateTx, localCandidateTy, localHits, localScore, isLocalHit) = ScoreLocalWindow(
            scaledPts,
            preparedFloor,
            lockedScale,
            priorTx,
            priorTy,
            bounds,
            cfg);
        swLocal.Stop();
        var localSearchMs = swLocal.Elapsed.TotalMilliseconds;

        double fallbackSearchMs = 0d;
        double localRefineMs = 0d;
        double verMs = 0d;
        double rfX = 0d;
        double rfY = 0d;
        double rfScore = 0d;
        Vpsg3SpatialResult spatial = default;
        bool isLocalFastPath = false;
        bool isAccepted = false;

        var validMask = observation.ValidMask;

        if (isLocalHit)
        {
            // Stage 4: Local Discrete Refinement with Scale Strictly Locked
            var swRefine = Stopwatch.StartNew();
            var (_, refinedX, refinedY, refinedScore, _) = Vpsg3LocalRefiner.Refine(
                sparsePoints, preparedFloor, lockedScale, localCandidateTx, localCandidateTy,
                bounds, width, height, lockScale: true);
            swRefine.Stop();
            localRefineMs = swRefine.Elapsed.TotalMilliseconds;

            // Stage 5: Joint Spatial Verification Gate
            var swVer = Stopwatch.StartNew();
            spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
                sparsePoints, validMask, preparedFloor, lockedScale, refinedX, refinedY, bounds, width, height);
            swVer.Stop();
            verMs = swVer.Elapsed.TotalMilliseconds;

            // Strict spatial check for local fast path: must have at least 2 passing quadrants
            if (spatial.IsSpatiallyConsistent && spatial.PassedPartitions >= 2 && spatial.GlobalScore >= 0.35d)
            {
                rfX = refinedX;
                rfY = refinedY;
                rfScore = refinedScore;
                isLocalFastPath = true;
                isAccepted = true;
            }
        }

        // If local candidate was absent or failed spatial consistency, fall back to global translation search
        if (!isAccepted)
        {
            // Stage 3: Elastic Recovery Fallback - Full Bitset Translation Search
            var swFallback = Stopwatch.StartNew();
            var (top1, runnerUp1, runnerUp2, _) = Vpsg3TranslationSolver.GenerateCandidates(
                observation, preparedFloor, lockedScale, scratch: sc);
            swFallback.Stop();
            fallbackSearchMs = swFallback.Elapsed.TotalMilliseconds;

            if (top1.RawScore < 5)
            {
                swTotal.Stop();
                var failTiming = new Vpsg3_5TrackingTiming(localSearchMs, fallbackSearchMs, localRefineMs, verMs, 0, swTotal.Elapsed.TotalMilliseconds);
                return Vpsg3_5TrackingResult.Reject("FallbackTranslationNoCandidates", lockedScale, priorTx, priorTy, failTiming);
            }

            // Evaluate Top 1 candidate
            var swRefine = Stopwatch.StartNew();
            var (_, bestX, bestY, bestScore, _) = Vpsg3LocalRefiner.Refine(
                sparsePoints, preparedFloor, lockedScale, top1.OffsetX, top1.OffsetY,
                bounds, width, height, lockScale: true);
            swRefine.Stop();
            localRefineMs += swRefine.Elapsed.TotalMilliseconds;

            var swVer = Stopwatch.StartNew();
            var bestSpatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
                sparsePoints, validMask, preparedFloor, lockedScale, bestX, bestY, bounds, width, height);

            // Re-ranking fallback: check runner-ups if Top 1 is not spatially consistent or has low score
            void CheckRunnerUp(Vpsg3TranslationCandidate? cand)
            {
                if (cand is null) return;
                var swR = Stopwatch.StartNew();
                var (_, candX, candY, candScore, _) = Vpsg3LocalRefiner.Refine(
                    sparsePoints, preparedFloor, lockedScale, cand.Value.OffsetX, cand.Value.OffsetY,
                    bounds, width, height, lockScale: true);
                swR.Stop();
                localRefineMs += swR.Elapsed.TotalMilliseconds;

                var candSpatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
                    sparsePoints, validMask, preparedFloor, lockedScale, candX, candY, bounds, width, height);

                if ((!bestSpatial.IsSpatiallyConsistent && candSpatial.IsSpatiallyConsistent)
                    || (candSpatial.IsSpatiallyConsistent && candSpatial.GlobalScore > bestSpatial.GlobalScore)
                    || (!bestSpatial.IsSpatiallyConsistent && !candSpatial.IsSpatiallyConsistent && candSpatial.GlobalScore > bestSpatial.GlobalScore))
                {
                    bestX = candX;
                    bestY = candY;
                    bestScore = candScore;
                    bestSpatial = candSpatial;
                }
            }

            if (!bestSpatial.IsSpatiallyConsistent || bestSpatial.PassedPartitions < 2 || bestSpatial.GlobalScore < cfg.MinVerificationScore)
            {
                if (runnerUp1.HasValue) CheckRunnerUp(runnerUp1);
                if ((!bestSpatial.IsSpatiallyConsistent || bestSpatial.PassedPartitions < 2 || bestSpatial.GlobalScore < cfg.MinVerificationScore) && runnerUp2.HasValue)
                {
                    CheckRunnerUp(runnerUp2);
                }
            }

            swVer.Stop();
            verMs += swVer.Elapsed.TotalMilliseconds;

            if (bestSpatial.IsSpatiallyConsistent && bestSpatial.PassedPartitions >= 2 && bestSpatial.GlobalScore >= cfg.MinVerificationScore)
            {
                rfX = bestX;
                rfY = bestY;
                rfScore = bestScore;
                spatial = bestSpatial;
                isLocalFastPath = false;
                isAccepted = true;
            }
            else
            {
                swTotal.Stop();
                var failTiming = new Vpsg3_5TrackingTiming(localSearchMs, fallbackSearchMs, localRefineMs, verMs, 0, swTotal.Elapsed.TotalMilliseconds);
                return Vpsg3_5TrackingResult.Reject($"SpatialVerificationFailed(Score={bestSpatial.GlobalScore:F3},Partitions={bestSpatial.PassedPartitions},Consistent={bestSpatial.IsSpatiallyConsistent})", lockedScale, priorTx, priorTy, failTiming);
            }
        }

        // Stage 6: Precision Sub-Pixel Distance-Field Refinement (with Scale Strictly Locked)
        var precisionMs = 0d;
        var finalX = rfX;
        var finalY = rfY;

        if (cfg.EnablePrecisionRefinement && !preparedFloor.PrecisionDistance.IsEmpty)
        {
            var swPrec = Stopwatch.StartNew();
            var budget = Vpsg3PrecisionBudget.Start();
            var precResult = Vpsg3PrecisionRefiner.Refine(
                observation, preparedFloor, lockedScale, rfX, rfY, budget, lockScale: true);
            swPrec.Stop();
            precisionMs = swPrec.Elapsed.TotalMilliseconds;

            if (precResult.Calibrated)
            {
                finalX = precResult.OffsetX;
                finalY = precResult.OffsetY;
            }
        }

        // Stage 7: Deadband Filter (Suppresses micro-jitter when stationary)
        var dDist = Math.Sqrt(Math.Pow(finalX - priorTx, 2) + Math.Pow(finalY - priorTy, 2));
        if (dDist < cfg.DeadbandPixels)
        {
            finalX = priorTx;
            finalY = priorTy;
        }
        else if (dDist > cfg.MaxTranslationJumpPixels)
        {
            swTotal.Stop();
            var failTiming = new Vpsg3_5TrackingTiming(localSearchMs, fallbackSearchMs, localRefineMs, verMs, precisionMs, swTotal.Elapsed.TotalMilliseconds);
            return Vpsg3_5TrackingResult.Reject(
                $"TranslationJumpTooLarge(Distance={dDist:F1}px,Max={cfg.MaxTranslationJumpPixels:F1}px)",
                lockedScale,
                priorTx,
                priorTy,
                failTiming);
        }

        swTotal.Stop();
        var totalMs = swTotal.Elapsed.TotalMilliseconds;
        var timing = new Vpsg3_5TrackingTiming(localSearchMs, fallbackSearchMs, localRefineMs, verMs, precisionMs, totalMs);

        return Vpsg3_5TrackingResult.Accept(
            scale: lockedScale,
            offsetX: finalX,
            offsetY: finalY,
            confidence: spatial.GlobalScore,
            score: rfScore,
            passedPartitions: spatial.PassedPartitions,
            isLocalFastPath: isLocalFastPath,
            timing: timing);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double CandidateTx, double CandidateTy, int Hits, double Score, bool Success) ScoreLocalWindow(
        ReadOnlySpan<Point> scaledPts,
        Vpsg3PreparedFloor preparedFloor,
        double lockedScale,
        double priorTx,
        double priorTy,
        MapScreenRect bounds,
        Vpsg3_5TrackingConfig cfg)
    {
        var invScale = 1.0d / lockedScale;
        var centerDx = (int)Math.Round((bounds.X - priorTx) * invScale);
        var centerDy = (int)Math.Round((bounds.Y - priorTy) * invScale);

        var radiusRef = (int)Math.Ceiling(cfg.LocalSearchRadius * invScale);
        var stride = Math.Max(1, cfg.CoarseStride);

        var minDx = centerDx - radiusRef;
        var maxDx = centerDx + radiusRef;
        var minDy = centerDy - radiusRef;
        var maxDy = centerDy + radiusRef;

        var wordsK3 = preparedFloor.DilatedBitsetK3Span;
        var wordsPerRow = preparedFloor.WordsPerRow;
        var refW = (uint)preparedFloor.ReferenceWidth;
        var refH = (uint)preparedFloor.ReferenceHeight;

        var bestDx = centerDx;
        var bestDy = centerDy;
        var bestHits = 0;
        var pointCount = scaledPts.Length;

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = 0;
                for (var i = 0; i < pointCount; i++)
                {
                    var rx = (uint)(scaledPts[i].X + dx);
                    var ry = (uint)(scaledPts[i].Y + dy);
                    if (rx < refW && ry < refH)
                    {
                        var wordIdx = (int)ry * wordsPerRow + (int)(rx >> 6);
                        if ((wordsK3[wordIdx] & (1UL << (int)(rx & 63))) != 0)
                        {
                            hits++;
                        }
                    }
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        // 3x3 Polish (+/-1px)
        var polishedDx = bestDx;
        var polishedDy = bestDy;
        var polishedHits = bestHits;
        for (var fdy = -1; fdy <= 1; fdy++)
        {
            for (var fdx = -1; fdx <= 1; fdx++)
            {
                if (fdx == 0 && fdy == 0) continue;
                var testDx = bestDx + fdx;
                var testDy = bestDy + fdy;
                var testHits = 0;
                for (var i = 0; i < pointCount; i++)
                {
                    var rx = (uint)(scaledPts[i].X + testDx);
                    var ry = (uint)(scaledPts[i].Y + testDy);
                    if (rx < refW && ry < refH)
                    {
                        var wordIdx = (int)ry * wordsPerRow + (int)(rx >> 6);
                        if ((wordsK3[wordIdx] & (1UL << (int)(rx & 63))) != 0)
                        {
                            testHits++;
                        }
                    }
                }
                if (testHits > polishedHits)
                {
                    polishedHits = testHits;
                    polishedDx = testDx;
                    polishedDy = testDy;
                }
            }
        }

        var candidateTx = bounds.X - polishedDx * lockedScale;
        var candidateTy = bounds.Y - polishedDy * lockedScale;
        var score = pointCount > 0 ? (double)polishedHits / pointCount : 0d;
        var success = score >= cfg.MinLocalScoreThreshold && polishedHits >= cfg.MinLocalHits;

        return (candidateTx, candidateTy, polishedHits, score, success);
    }
}
