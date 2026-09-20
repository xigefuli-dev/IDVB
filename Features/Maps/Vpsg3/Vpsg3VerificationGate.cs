using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production verification gate for VPSG 3.0.
/// Enforces joint multi-signal gating:
/// 1. S-B PeakRatio threshold
/// 2. K5 distinct aperture margin (&gt;= MinApertureMargin) — the sole discriminability
///    gate, never relaxed by spatial-consistency exemptions
/// 3. Weighted convex verification score, jointly determined by the aperture margin
///    (wider margin tolerates a lower score)
/// 4. 2x2 spatial quadrant consistency (PassedPartitions &gt;= MinPassedPartitions, with
///    small-observation exemptions for dominant-single-quadrant and high-confidence globals)
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

        var maxPartPoints = 0;
        var maxPartPassed = false;
        for (var p = 0; p < 4; p++)
        {
            if (partTotal[p] > maxPartPoints)
            {
                maxPartPoints = partTotal[p];
                maxPartPassed = (double)partHits[p] / partTotal[p] >= cfg.PartitionScoreThreshold;
            }

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

        // 空间一致性 / 质量放行判定：
        // 1. 标准多分区：通过的分区数 >= MinPassedPartitions (默认 2)；
        // 2. 全部有效分区均通过且全局得分达标 (如单分区完全覆盖)；
        // 3. 主导象限高质量覆盖：特征点高度集中在单象限 (>= 60%) 且该象限自身通过，同时全局得分与命中点充分 (totalHits >= 20 且 globalScore >= PartitionScoreThreshold)；
        // 4. 全局高置信放行：全局得分 >= 0.55 且总命中点数 >= 25，至少有 1 个分区通过 (passedParts >= 1)，避免边缘局部视角下被零碎杂质象限误杀。
        var isDominantPartitionValid = maxPartPoints >= totalValidPoints * 0.60
            && maxPartPassed
            && globalScore >= cfg.PartitionScoreThreshold
            && totalHits >= 20;

        var isHighConfidenceGlobalValid = globalScore >= 0.55d
            && totalHits >= 25
            && passedParts >= 1;

        var isConsistent = passedParts >= cfg.MinPassedPartitions
            || (validParts > 0 && passedParts == validParts && globalScore >= cfg.PartitionScoreThreshold)
            || isDominantPartitionValid
            || isHighConfidenceGlobalValid;
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

        var dX = 0d;
        var dY = 0d;
        var refinedDist = 0d;
        if (hasDistinctRunnerUp && runnerUpCandidate.HasValue)
        {
            dX = bestCandidate.OffsetX - runnerUpCandidate.Value.OffsetX;
            dY = bestCandidate.OffsetY - runnerUpCandidate.Value.OffsetY;
            refinedDist = Math.Sqrt(dX * dX + dY * dY);
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

        // Gate 2: Aperture Margin —— 真实位置歧义的唯一判据，不接受任何豁免。
        // 此前该门槛会在 IsSpatiallyConsistent 成立时被降到 0.035d，而空间一致性本身
        // 有四条宽松放行路径（其中 isKnownScale 分支因 knownScaleSeed 路径硬编码
        // PeakRatio = 10.0d 而恒真）。小观测几乎必然满足这些豁免，判别力检查形同虚设，
        // "主峰与次峰几乎并列"的错解被放行并经 VpsgDirectLock 锁死整局。
        // 观测区域小只应放宽分区要求，绝不应放宽判别力要求。
        if (margin < cfg.MinApertureMargin)
        {
            return new Vpsg3GateResult(false, margin, hasValidCompetitor, $"ApertureMarginBelowThreshold: {margin:F3} < {cfg.MinApertureMargin:F3} (2ndScore={runnerUpScore:F3})");
        }

        // Gate 4: Joint verification score —— 判别力与验证分联合判定。
        // margin 宽裕说明主峰与次峰分离明确，可容忍更低的加权验证分；单房间等小观测的
        // 有效点少、验证分天然偏低，此前被固定 0.50 硬门槛系统性误杀。
        // margin 贴近下限时则要求完整的 MinVerificationScore 佐证。
        var requiredScore = ResolveRequiredVerificationScore(margin, cfg);
        if (bestCandidate.WeightedScore < requiredScore)
        {
            return new Vpsg3GateResult(false, margin, true, $"VerificationScoreBelowThreshold: {bestCandidate.WeightedScore:F3} < {requiredScore:F3} (margin={margin:F3})");
        }

        // Gate 5: Spatial 2x2 Quadrant Consistency
        if (!bestCandidate.Spatial.IsSpatiallyConsistent)
        {
            return new Vpsg3GateResult(false, margin, true, $"SpatialPartitionsBelowThreshold: {bestCandidate.Spatial.PassedPartitions} < {cfg.MinPassedPartitions}");
        }

        // Gate 6: Canonical Transform Sanity (ensures viewport overlaps substantially with reference space)
        var invScale = 1.0d / bestCandidate.Scale;
        var minRefX = (viewportBounds.X - bestCandidate.OffsetX) * invScale;
        var maxRefX = (viewportBounds.X + viewportBounds.Width - bestCandidate.OffsetX) * invScale;
        var minRefY = (viewportBounds.Y - bestCandidate.OffsetY) * invScale;
        var maxRefY = (viewportBounds.Y + viewportBounds.Height - bestCandidate.OffsetY) * invScale;

        var overlapW = Math.Min(maxRefX, referenceWidth) - Math.Max(minRefX, 0);
        var overlapH = Math.Min(maxRefY, referenceHeight) - Math.Max(minRefY, 0);

        if (overlapW < 50 || overlapH < 50)
        {
            return new Vpsg3GateResult(false, margin, true, $"TransformedBoundsOutOfBounds: overlap=({overlapW:F0}x{overlapH:F0})");
        }

        return new Vpsg3GateResult(true, margin, true, string.Empty);
    }

    /// <summary>
    /// Resolves the weighted verification score required at a given aperture margin.
    /// At the margin floor the full <see cref="Vpsg3TuningConfig.MinVerificationScore"/>
    /// is required; as the margin widens toward
    /// <see cref="Vpsg3TuningConfig.MarginRelaxationSpan"/> the requirement relaxes by up
    /// to <see cref="Vpsg3TuningConfig.MaxVerificationScoreRelief"/>.
    /// </summary>
    private static double ResolveRequiredVerificationScore(
        double margin,
        Vpsg3TuningConfig cfg)
    {
        var span = cfg.MarginRelaxationSpan;
        if (!double.IsFinite(span) || span <= 0d)
        {
            return cfg.MinVerificationScore;
        }

        var relief = Math.Clamp(
            (margin - cfg.MinApertureMargin) / span,
            0d,
            1d);
        return cfg.MinVerificationScore
            - relief * Math.Max(0d, cfg.MaxVerificationScoreRelief);
    }
}
