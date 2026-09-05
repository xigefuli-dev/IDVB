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
public static class Vpsg3TranslationSolver
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
        sc.CandidateCount = 0;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cfg.CoarseTranslationStride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cfg.MaxSparsePoints);

        var sparsePoints = observation.SparseEdgePoints;
        var pointCount = sparsePoints.Count;
        if (pointCount == 0 || !double.IsFinite(estimatedScale) || estimatedScale <= 0.01d)
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

        // A passing weighted score needs at least this many in-reference points.
        // Quantile bounds include every such transform, including padded viewports.
        Span<int> xs = stackalloc int[limitPts];
        Span<int> ys = stackalloc int[limitPts];
        for (var i = 0; i < limitPts; i++) { xs[i] = scaledPts[i].X; ys[i] = scaledPts[i].Y; }
        xs.Sort(); ys.Sort();
        var misses = Math.Clamp(limitPts - (int)Math.Ceiling(cfg.MinVerificationScore * limitPts), 0, limitPts - 1);
        var minDx = -xs[misses] - 2;
        var maxDx = refW + 1 - xs[limitPts - 1 - misses];
        var minDy = -ys[misses] - 2;
        var maxDy = refH + 1 - ys[limitPts - 1 - misses];
        if (maxDx < minDx || maxDy < minDy) return (default, null, null, false);
        var columns = (maxDx - minDx) / stride + 1;
        var scoreCount = checked(columns * ((maxDy - minDy) / stride + 1));
        if (sc.TranslationScores.Length < scoreCount)
            sc.TranslationScores = new int[scoreCount];
        var scores = sc.TranslationScores.AsSpan(0, scoreCount);
        ScoreCoarseGrid(scaledPts, preparedFloor, maxDx, maxDy, stride, scores, minDx, minDy);

        // 2. Coarse 2D translation grid search across reference space maintaining Top-32 candidates
        Span<CoarseCand> topPool = stackalloc CoarseCand[32];
        var poolCount = 0;

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = scores[(dy - minDy) / stride * columns + (dx - minDx) / stride];

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
                scaledPts, preparedFloor, topPool[i].Dx, topPool[i].Dy, maxDx, maxDy, minDx, minDy);
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
        // Retain the already-scored pool so merged refined peaks can be replaced
        // without correlating the reference grid again.
        for (var i = 0; i < distinctCount; i++)
        {
            var c = distinctPeaks[i];
            sc.CandidateBuffer[i] = new Vpsg3TranslationCandidate(
                observation.ViewportBounds.X - c.Dx * estimatedScale,
                observation.ViewportBounds.Y - c.Dy * estimatedScale, c.Hits, i + 1);
        }
        sc.CandidateCount = distinctCount;
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

                    var hits = scores[(dy - minDy) / stride * columns + (dx - minDx) / stride];

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
                    scaledPts, preparedFloor, runnerUpDx, runnerUpDy, maxDx, maxDy, minDx, minDy);

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

    // Each bit lane counts one translation. Binary addition scores 64 offsets together,
    // preserving integer scores, grid order and ties; the runner-up reuses the same grid.
    internal static void ScoreCoarseGrid(ReadOnlySpan<Point> points, Vpsg3PreparedFloor floor,
        int maxDx, int maxDy, int stride, Span<int> scores, int minDx = 0, int minDy = 0)
    {
        var words = floor.DilatedBitsetK3Span;
        var wordsPerRow = floor.WordsPerRow;
        var columns = (maxDx - minDx) / stride + 1;
        Span<ulong> planes = stackalloc ulong[9]; // Up to 256 sampled points.
        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var blockX = minDx; blockX <= maxDx; blockX += 64)
            {
                planes.Clear();
                foreach (var point in points)
                {
                    var x = point.X + blockX;
                    var y = point.Y + dy;
                    if ((uint)y >= (uint)floor.ReferenceHeight || x >= floor.ReferenceWidth || x <= -64)
                        continue;
                    ulong carry;
                    if (x < 0)
                    {
                        carry = words[y * wordsPerRow] << -x;
                    }
                    else
                    {
                        var wordX = x >> 6;
                        var shift = x & 63;
                        carry = words[y * wordsPerRow + wordX] >> shift;
                        if (shift != 0 && wordX + 1 < wordsPerRow)
                            carry |= words[y * wordsPerRow + wordX + 1] << (64 - shift);
                    }
                    var remaining = floor.ReferenceWidth - x;
                    if (remaining < 64)
                        carry &= (1UL << remaining) - 1;
                    for (var bit = 0; carry != 0; bit++)
                    {
                        var nextCarry = planes[bit] & carry;
                        planes[bit] ^= carry;
                        carry = nextCarry;
                    }
                }

                var first = (stride - (blockX - minDx) % stride) % stride;
                var last = Math.Min(63, maxDx - blockX);
                for (var lane = first; lane <= last; lane += stride)
                {
                    var hits = 0;
                    for (var bit = 0; bit < planes.Length; bit++)
                        hits |= (int)((planes[bit] >> lane) & 1) << bit;
                    scores[(dy - minDy) / stride * columns + (blockX + lane - minDx) / stride] = hits;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCoarseCandidate(
        Span<CoarseCand> pool,
        ref int count,
        int dx,
        int dy,
        int hits)
    {
        // Grid coordinates are unique. Reject noncompetitive scores before sorting.
        if (count == pool.Length && hits <= pool[count - 1].Hits) return;

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
        int maxDy,
        int minDx,
        int minDy)
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

        var startY = Math.Max(minDy, centerDy - 2);
        var endY = Math.Min(maxDy, centerDy + 2);
        var startX = Math.Max(minDx, centerDx - 2);
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
