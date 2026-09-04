using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production translation candidate generator for VPSG 3.0 (Method T-3).
/// Operates directly on resident PreparedFloor 64-bit row-major bitsets.
/// Prohibits runtime Mat resizing, DistanceTransform, or MatchTemplate.
/// Generates Top-1 candidate and distinct Top-2 runner-up with spatial NMS suppression.
/// </summary>
internal static class Vpsg3TranslationScalarBaseline
{
    /// <summary>
    /// Generates translation candidates and detects presence of a distinct spatial runner-up.
    /// </summary>
    public static (Vpsg3TranslationCandidate Top1, Vpsg3TranslationCandidate? DistinctRunnerUp, Vpsg3TranslationCandidate? DistinctRunnerUp2, bool HasDistinctRunnerUp) GenerateCandidates(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        double estimatedScale,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3TuningConfig.Default;
        var sc = scratch ?? Vpsg3SolverScratch.Current;

        var sparsePoints = observation.SparseEdgePoints;
        var pointCount = sparsePoints.Count;
        if (pointCount == 0 || estimatedScale <= 0.01d)
        {
            return (default, null, null, false);
        }

        // 1. Scale sparse query points to reference scale into scratch buffer (zero allocation)
        var limitPts = Math.Min(pointCount, Math.Min(cfg.MaxSparsePoints, sc.ScaledQueryPointsBuffer.Length));
        var scaledPts = sc.ScaledQueryPointsBuffer.AsSpan(0, limitPts);
        var invScale = 1.0d / estimatedScale;

        for (var i = 0; i < limitPts; i++)
        {
            var p = sparsePoints[i];
            scaledPts[i] = new Point(
                (int)Math.Round(p.X * invScale),
                (int)Math.Round(p.Y * invScale));
        }

        var refW = preparedFloor.ReferenceWidth;
        var refH = preparedFloor.ReferenceHeight;
        var stride = cfg.CoarseTranslationStride;

        var queryWInRef = (int)Math.Round(observation.Width * invScale);
        var queryHInRef = (int)Math.Round(observation.Height * invScale);

        var minDx = 0;
        var maxDx = Math.Max(0, refW - queryWInRef);
        var minDy = 0;
        var maxDy = Math.Max(0, refH - queryHInRef);

        // 2. Coarse 2D translation grid search across reference space maintaining Top-32 candidates
        Span<CoarseCand> topPool = stackalloc CoarseCand[32];
        var poolCount = 0;

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = 0;
                for (var i = 0; i < limitPts; i++)
                {
                    var rx = scaledPts[i].X + dx;
                    var ry = scaledPts[i].Y + dy;
                    if (preparedFloor.IsHitK3(rx, ry))
                    {
                        hits++;
                    }
                }

                if (hits >= 5)
                {
                    InsertCoarseCandidate(topPool, ref poolCount, dx, dy, hits);
                }
            }
        }

        if (poolCount == 0 || topPool[0].Hits < 5)
        {
            return (default, null, null, false);
        }

        // 3. Sub-window +/-2px polish for all pool candidates to eliminate coarse grid quantization error
        Span<CoarseCand> polished = stackalloc CoarseCand[poolCount];
        for (var i = 0; i < poolCount; i++)
        {
            var (pDx, pDy, pHits) = PolishTranslationSubWindow(
                scaledPts, preparedFloor, topPool[i].Dx, topPool[i].Dy, maxDx, maxDy);
            polished[i] = new CoarseCand(pDx, pDy, pHits);
        }
        SortCoarsePool(polished);

        // 4. Post-polish deduplication into distinct physical peaks
        Span<CoarseCand> distinctPeaks = stackalloc CoarseCand[poolCount];
        var distinctCount = 0;
        for (var i = 0; i < poolCount; i++)
        {
            var cand = polished[i];
            var isDuplicate = false;
            for (var j = 0; j < distinctCount; j++)
            {
                var existing = distinctPeaks[j];
                // In reference space, candidates within 2px are samples of the same physical peak
                if (Math.Abs(cand.Dx - existing.Dx) <= 2 && Math.Abs(cand.Dy - existing.Dy) <= 2)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                distinctPeaks[distinctCount++] = cand;
            }
        }

        var best = distinctPeaks[0];
        var top1Offset = new Vpsg3TranslationCandidate(
            OffsetX: observation.ViewportBounds.X - best.Dx * estimatedScale,
            OffsetY: observation.ViewportBounds.Y - best.Dy * estimatedScale,
            RawScore: best.Hits,
            Rank: 1);

        // 5. Distinct runner-up selection outside NMS suppression basin of best
        var minDistinctDistRef = cfg.MinDistinctDistance * invScale;
        var minDistinctDistSqRef = minDistinctDistRef * minDistinctDistRef;

        Vpsg3TranslationCandidate? runnerUpCandidate1 = null;
        Vpsg3TranslationCandidate? runnerUpCandidate2 = null;

        for (var i = 1; i < distinctCount; i++)
        {
            var c = distinctPeaks[i];
            var distSq = (c.Dx - best.Dx) * (c.Dx - best.Dx) + (c.Dy - best.Dy) * (c.Dy - best.Dy);
            if (distSq >= minDistinctDistSqRef && c.Hits >= 3)
            {
                if (runnerUpCandidate1 is null)
                {
                    runnerUpCandidate1 = new Vpsg3TranslationCandidate(
                        OffsetX: observation.ViewportBounds.X - c.Dx * estimatedScale,
                        OffsetY: observation.ViewportBounds.Y - c.Dy * estimatedScale,
                        RawScore: c.Hits,
                        Rank: 2);
                }
                else
                {
                    runnerUpCandidate2 = new Vpsg3TranslationCandidate(
                        OffsetX: observation.ViewportBounds.X - c.Dx * estimatedScale,
                        OffsetY: observation.ViewportBounds.Y - c.Dy * estimatedScale,
                        RawScore: c.Hits,
                        Rank: 3);
                    break;
                }
            }
        }

        // If not found in polished pool, search coarse grid outside NMS basin
        if (runnerUpCandidate1 is null)
        {
            var runnerUpDx = 0;
            var runnerUpDy = 0;
            var runnerUpHits = 0;
            var foundRunnerUp = false;

            for (var dy = minDy; dy <= maxDy; dy += stride)
            {
                for (var dx = minDx; dx <= maxDx; dx += stride)
                {
                    var distSq = (dx - best.Dx) * (dx - best.Dx) + (dy - best.Dy) * (dy - best.Dy);
                    if (distSq < minDistinctDistSqRef)
                        continue;

                    var hits = 0;
                    for (var i = 0; i < limitPts; i++)
                    {
                        var rx = scaledPts[i].X + dx;
                        var ry = scaledPts[i].Y + dy;
                        if (preparedFloor.IsHitK3(rx, ry)) hits++;
                    }

                    if (hits > runnerUpHits)
                    {
                        runnerUpHits = hits;
                        runnerUpDx = dx;
                        runnerUpDy = dy;
                        foundRunnerUp = true;
                    }
                }
            }

            if (foundRunnerUp && runnerUpHits >= 3)
            {
                var (pDx2, pDy2, pHits2) = PolishTranslationSubWindow(
                    scaledPts, preparedFloor, runnerUpDx, runnerUpDy, maxDx, maxDy);

                var pDistSq = (pDx2 - best.Dx) * (pDx2 - best.Dx) + (pDy2 - best.Dy) * (pDy2 - best.Dy);
                if (pDistSq >= minDistinctDistSqRef)
                {
                    runnerUpCandidate1 = new Vpsg3TranslationCandidate(
                        OffsetX: observation.ViewportBounds.X - pDx2 * estimatedScale,
                        OffsetY: observation.ViewportBounds.Y - pDy2 * estimatedScale,
                        RawScore: pHits2,
                        Rank: 2);
                }
            }
        }

        return (top1Offset, runnerUpCandidate1, runnerUpCandidate2, runnerUpCandidate1.HasValue);
    }

    private readonly record struct CoarseCand(int Dx, int Dy, int Hits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCoarseCandidate(
        Span<CoarseCand> pool,
        ref int count,
        int dx,
        int dy,
        int hits)
    {
        for (var i = 0; i < count; i++)
        {
            var c = pool[i];
            if (c.Dx == dx && c.Dy == dy)
            {
                if (hits > c.Hits)
                {
                    pool[i] = new CoarseCand(dx, dy, hits);
                    SortCoarsePool(pool.Slice(0, count));
                }
                return;
            }
        }

        if (count < pool.Length)
        {
            pool[count++] = new CoarseCand(dx, dy, hits);
            SortCoarsePool(pool.Slice(0, count));
        }
        else if (hits > pool[pool.Length - 1].Hits)
        {
            pool[pool.Length - 1] = new CoarseCand(dx, dy, hits);
            SortCoarsePool(pool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SortCoarsePool(Span<CoarseCand> pool)
    {
        for (var i = 1; i < pool.Length; i++)
        {
            var key = pool[i];
            var j = i - 1;
            while (j >= 0 && pool[j].Hits < key.Hits)
            {
                pool[j + 1] = pool[j];
                j--;
            }
            pool[j + 1] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int PolishedDx, int PolishedDy, int BestHits) PolishTranslationSubWindow(
        ReadOnlySpan<Point> scaledPts,
        Vpsg3PreparedFloor preparedFloor,
        int centerDx,
        int centerDy,
        int maxDx,
        int maxDy)
    {
        var bestDx = centerDx;
        var bestDy = centerDy;
        var bestHits = 0;
        for (var i = 0; i < scaledPts.Length; i++)
        {
            var rx = scaledPts[i].X + centerDx;
            var ry = scaledPts[i].Y + centerDy;
            if (preparedFloor.IsHitK3(rx, ry))
            {
                bestHits++;
            }
        }

        var startY = Math.Max(0, centerDy - 2);
        var endY = Math.Min(maxDy, centerDy + 2);
        var startX = Math.Max(0, centerDx - 2);
        var endX = Math.Min(maxDx, centerDx + 2);

        for (var ldy = startY; ldy <= endY; ldy++)
        {
            for (var ldx = startX; ldx <= endX; ldx++)
            {
                if (ldx == centerDx && ldy == centerDy)
                    continue;

                var hits = 0;
                for (var i = 0; i < scaledPts.Length; i++)
                {
                    var rx = scaledPts[i].X + ldx;
                    var ry = scaledPts[i].Y + ldy;
                    if (preparedFloor.IsHitK3(rx, ry))
                    {
                        hits++;
                    }
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestDx = ldx;
                    bestDy = ldy;
                }
            }
        }

        return (bestDx, bestDy, bestHits);
    }
}

