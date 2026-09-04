using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production verification gate for VPSG 3.0.
/// Enforces joint multi-signal gating:
/// 1. S-B PeakRatio threshold
/// 2. Weighted convex verification score
/// 3. K5 distinct aperture margin (&gt;= 0.09)
/// 4. 2x2 spatial quadrant consistency (PassedPartitions &gt;= 3)
/// 5. ValidMask neutrality: unknown fog pixels are neutral (neither hit nor miss)
/// 6. Strict aperture uniqueness: if DistinctRunnerUpFound is false, rejects fake margins.
/// </summary>
public static class Vpsg3VerificationGate
{
    /// <summary>
    /// Evaluates 2x2 spatial quadrant consistency and global K5 hit score.
    /// Incorporates ValidMask neutrality: points with ValidMask == 0 are skipped entirely.
    /// </summary>
    public static Vpsg3SpatialResult EvaluateSpatialVerification(
        IReadOnlyList<Point> sparsePoints,
        Mat validMask,
        Vpsg3PreparedFloor preparedFloor,
        double scale,
        double offsetX,
        double offsetY,
        MapScreenRect viewportBounds,
        int width,
        int height,
        Vpsg3TuningConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(preparedFloor);
        var totalPoints = sparsePoints?.Count ?? 0;
        if (totalPoints == 0 || scale <= 0.01d)
        {
            return new Vpsg3SpatialResult(0d, 0, 0, 0, 0, false);
        }

        var cfg = config ?? Vpsg3TuningConfig.Default;
        var halfW = width / 2;
        var halfH = height / 2;
        var invScale = 1.0d / scale;

        // 2x2 Quadrant counters: [TL(0), TR(1), BL(2), BR(3)]
        Span<int> partTotal = stackalloc int[4];
        Span<int> partHits = stackalloc int[4];
        partTotal.Clear();
        partHits.Clear();
        var totalValidPoints = 0;
        var totalHits = 0;

        for (var i = 0; i < totalPoints; i++)
        {
            var q = sparsePoints![i];

            // ValidMask neutrality check: unknown/fog/HUD pixels are neutral
            if (validMask is not null && !validMask.IsDisposed)
            {
                if (q.X >= 0 && q.X < width && q.Y >= 0 && q.Y < height)
                {
                    if (validMask.At<byte>(q.Y, q.X) < 128)
                    {
                        // Point is in unknown/fog region: skip from numerator AND denominator
                        continue;
                    }
                }
            }

            var partIdx = (q.X < halfW ? 0 : 1) + (q.Y < halfH ? 0 : 2);
            partTotal[partIdx]++;
            totalValidPoints++;

            var screenX = viewportBounds.X + q.X;
            var screenY = viewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - offsetX) * invScale);
            var ry = (int)Math.Round((screenY - offsetY) * invScale);

            if (preparedFloor.IsHitK5(rx, ry))
            {
                totalHits++;
                partHits[partIdx]++;
            }
        }

        if (totalValidPoints == 0)
        {
            return new Vpsg3SpatialResult(0d, 0, 0, 0, 0, false);
        }

        var globalScore = (double)totalHits / totalValidPoints;
        var validParts = 0;
        var passedParts = 0;

        for (var p = 0; p < 4; p++)
        {
            if (partTotal[p] >= cfg.MinPointsPerPartition)
            {
                validParts++;
                var ratio = (double)partHits[p] / partTotal[p];
                if (ratio >= cfg.PartitionScoreThreshold)
                {
                    passedParts++;
                }
            }
        }

        var isConsistent = passedParts >= cfg.MinPassedPartitions;
        return new Vpsg3SpatialResult(globalScore, totalValidPoints, totalHits, validParts, passedParts, isConsistent);
    }

    /// <summary>
    /// Evaluates final joint gate decision for candidate acceptance.
    /// </summary>
    public static Vpsg3GateResult EvaluateDecision(
        Vpsg3ScaleResult scaleResult,
        Vpsg3RefinedCandidate bestCandidate,
        Vpsg3RefinedCandidate? runnerUpCandidate,
        bool hasDistinctRunnerUp,
        MapScreenRect viewportBounds,
        int referenceWidth,
        int referenceHeight,
        Vpsg3TuningConfig? config = null)
    {
        var cfg = config ?? Vpsg3TuningConfig.Default;

        // Gate 1: Scale prior health
        if (!scaleResult.Success)
        {
            return new Vpsg3GateResult(false, 0d, hasDistinctRunnerUp, $"ScalePriorFailed: {scaleResult.RejectReason}");
        }

        if (scaleResult.PeakRatio < cfg.PeakRatioThreshold)
        {
            return new Vpsg3GateResult(false, 0d, hasDistinctRunnerUp, $"ScalePeakRatioBelowThreshold: {scaleResult.PeakRatio:F2} < {cfg.PeakRatioThreshold:F2}");
        }

        if (!double.IsFinite(bestCandidate.Scale) || !double.IsFinite(bestCandidate.OffsetX)
            || !double.IsFinite(bestCandidate.OffsetY) || !double.IsFinite(bestCandidate.WeightedScore)
            || !double.IsFinite(bestCandidate.Spatial.GlobalScore)
            || bestCandidate.Scale < cfg.MinSupportedScale || bestCandidate.Scale > cfg.MaxSupportedScale)
        {
            return new Vpsg3GateResult(false, 0d, hasDistinctRunnerUp, $"RefinedScaleOutOfRange: {bestCandidate.Scale:F4}");
        }

        // Gate 2: Aperture Margin (K5 Global Score difference against valid distinct competing peak >= 6px)
        var runnerUpScore = 0.0d;
        var hasValidCompetitor = false;

        if (hasDistinctRunnerUp && runnerUpCandidate.HasValue)
        {
            var dX = bestCandidate.OffsetX - runnerUpCandidate.Value.OffsetX;
            var dY = bestCandidate.OffsetY - runnerUpCandidate.Value.OffsetY;
            var refinedDist = Math.Sqrt(dX * dX + dY * dY);
            if (double.IsFinite(refinedDist) && refinedDist >= cfg.MinDistinctDistance
                && double.IsFinite(runnerUpCandidate.Value.Spatial.GlobalScore))
            {
                runnerUpScore = runnerUpCandidate.Value.Spatial.GlobalScore;
                hasValidCompetitor = true;
            }
        }

        if (!hasValidCompetitor)
            return new Vpsg3GateResult(false, 0d, false, "NoDistinctRefinedRunnerUp");

        var margin = bestCandidate.Spatial.GlobalScore - runnerUpScore;
        if (margin < cfg.MinApertureMargin)
        {
            return new Vpsg3GateResult(false, margin, hasValidCompetitor, $"ApertureMarginBelowThreshold: {margin:F3} < {cfg.MinApertureMargin:F3} (2ndScore={runnerUpScore:F3})");
        }

        // Gate 4: Global Verification Score
        if (bestCandidate.WeightedScore < cfg.MinVerificationScore)
        {
            return new Vpsg3GateResult(false, margin, true, $"VerificationScoreBelowThreshold: {bestCandidate.WeightedScore:F3} < {cfg.MinVerificationScore:F3}");
        }

        // Gate 5: Spatial 2x2 Quadrant Consistency
        if (bestCandidate.Spatial.PassedPartitions < cfg.MinPassedPartitions)
        {
            return new Vpsg3GateResult(false, margin, true, $"SpatialPartitionsBelowThreshold: {bestCandidate.Spatial.PassedPartitions} < {cfg.MinPassedPartitions}");
        }

        // Gate 6: Canonical Transform Sanity (ensures viewport overlaps substantially with reference space)
        var invScale = 1.0d / bestCandidate.Scale;
        var minRefX = (int)Math.Round((viewportBounds.X - bestCandidate.OffsetX) * invScale);
        var minRefY = (int)Math.Round((viewportBounds.Y - bestCandidate.OffsetY) * invScale);
        if (minRefX < -50 || minRefX > referenceWidth + 50 || minRefY < -50 || minRefY > referenceHeight + 50)
        {
            return new Vpsg3GateResult(false, margin, true, $"TransformedBoundsOutOfBounds: refOrigin=({minRefX}, {minRefY})");
        }

        return new Vpsg3GateResult(true, margin, true, string.Empty);
    }
}
