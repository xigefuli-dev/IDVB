using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production local refiner for VPSG 3.0.
/// Employs centered joint refinement (up to 277 probes) with convex weighted potential field
/// scoring: (hits_K5 + 2 * hits_K3) / (3 * N).
/// Centered scaling parameterization preserves canonical transform coordinate semantics.
/// </summary>
public static class Vpsg3LocalRefiner
{
    private static readonly double[] ScaleCoarseDeltas = [-0.020d, -0.015d, 0.000d, 0.015d, 0.020d];
    private static readonly double[] TranslationCoarseDeltas = [-6.0d, -4.0d, -2.0d, 0.0d, 2.0d, 4.0d, 6.0d];
    private static readonly double[] ScaleScanDeltas = [-0.020d, -0.010d, -0.005d, 0.005d, 0.010d, 0.020d];
    private static readonly double[] TranslationFineDeltas = [-1.5d, -1.0d, -0.5d, 0.0d, 0.5d, 1.0d, 1.5d];
    private static readonly double[] ScaleFineDeltas = [-0.005d, 0.0d, 0.005d];

    /// <summary>
    /// Refines scale and translation from initial seed estimates using resident dual bitsets.
    /// </summary>
    public static (double RefinedScale, double RefinedX, double RefinedY, double BestScore, int Probes) Refine(
        IReadOnlyList<Point> sparsePoints,
        Vpsg3PreparedFloor preparedFloor,
        double seedScale,
        double seedX,
        double seedY,
        MapScreenRect viewportBounds,
        int width,
        int height,
        bool lockScale = false)
    {
        ArgumentNullException.ThrowIfNull(preparedFloor);
        var pointCount = sparsePoints?.Count ?? 0;
        if (pointCount == 0)
        {
            return (seedScale, seedX, seedY, 0.0d, 0);
        }

        var cx = viewportBounds.X + width / 2.0d;
        var cy = viewportBounds.Y + height / 2.0d;
        var rcx = (cx - seedX) / seedScale;
        var rcy = (cy - seedY) / seedScale;

        var probes = 0;

        if (lockScale)
        {
            // 稳态路径：已有尺度种子，严格锁定尺度，严禁量化噪声或局部视野引发单帧微漂移！
            // 仅在此尺度执行平移搜索（49 次粗搜 + 48 次细搜 + 8 次 0.25px 亚像素精修）
            var bestScoreLocked = EvaluateScore(sparsePoints!, preparedFloor, seedScale, seedX, seedY, viewportBounds, -1.0d);
            var bXL = seedX;
            var bYL = seedY;

            for (var i = 0; i < TranslationCoarseDeltas.Length; i++)
            {
                var dx = TranslationCoarseDeltas[i];
                for (var j = 0; j < TranslationCoarseDeltas.Length; j++)
                {
                    var dy = TranslationCoarseDeltas[j];
                    if (dx == 0.0d && dy == 0.0d) continue;
                    probes++;
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, seedScale, seedX + dx, seedY + dy, viewportBounds, bestScoreLocked);
                    if (sc > bestScoreLocked)
                    {
                        bestScoreLocked = sc;
                        bXL = seedX + dx;
                        bYL = seedY + dy;
                    }
                }
            }

            var finalXL = bXL;
            var finalYL = bYL;
            for (var xIdx = 0; xIdx < TranslationFineDeltas.Length; xIdx++)
            {
                var fdx = TranslationFineDeltas[xIdx];
                for (var yIdx = 0; yIdx < TranslationFineDeltas.Length; yIdx++)
                {
                    var fdy = TranslationFineDeltas[yIdx];
                    if (fdx == 0.0d && fdy == 0.0d) continue;
                    probes++;
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, seedScale, bXL + fdx, bYL + fdy, viewportBounds, bestScoreLocked);
                    if (sc > bestScoreLocked)
                    {
                        bestScoreLocked = sc;
                        finalXL = bXL + fdx;
                        finalYL = bYL + fdy;
                    }
                }
            }

            // 亚像素 0.25px 步长精修（8 probes）
            for (var sx = -1; sx <= 1; sx++)
            {
                for (var sy = -1; sy <= 1; sy++)
                {
                    if (sx == 0 && sy == 0) continue;
                    var subX = finalXL + sx * 0.25d;
                    var subY = finalYL + sy * 0.25d;
                    probes++;
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, seedScale, subX, subY, viewportBounds, bestScoreLocked);
                    if (sc > bestScoreLocked)
                    {
                        bestScoreLocked = sc;
                        finalXL = subX;
                        finalYL = subY;
                    }
                }
            }

            return (seedScale, finalXL, finalYL, bestScoreLocked, probes);
        }

        // Stage 1: Coarse joint grid (5 scales x 7x7 translations = 245 probes).
        var bS = seedScale;
        var bX = seedX;
        var bY = seedY;
        var bestScore = EvaluateScore(sparsePoints!, preparedFloor, seedScale, seedX, seedY, viewportBounds, -1.0d);

        for (var sIdx = 0; sIdx < ScaleCoarseDeltas.Length; sIdx++)
        {
            var ds = ScaleCoarseDeltas[sIdx];
            var cs = seedScale + ds;
            if (cs < 0.35d || cs > 2.50d) continue;

            var bx = cx - rcx * cs;
            var by = cy - rcy * cs;

            for (var i = 0; i < TranslationCoarseDeltas.Length; i++)
            {
                var dx = TranslationCoarseDeltas[i];
                for (var j = 0; j < TranslationCoarseDeltas.Length; j++)
                {
                    var dy = TranslationCoarseDeltas[j];
                    probes++;
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, cs, bx + dx, by + dy, viewportBounds, bestScore);
                    if (sc > bestScore)
                    {
                        bestScore = sc;
                        bS = cs;
                        bX = bx + dx;
                        bY = by + dy;
                    }
                }
            }
        }

        // Stage 2: Scale fine scan around best translation using centered formula
        var rCentX = (cx - bX) / bS;
        var rCentY = (cy - bY) / bS;
        var bS2 = bS;
        var bX2 = bX;
        var bY2 = bY;

        for (var i = 0; i < ScaleScanDeltas.Length; i++)
        {
            var ds = ScaleScanDeltas[i];
            var cs = bS + ds;
            if (cs < 0.35d || cs > 2.50d) continue;

            var nx = cx - rCentX * cs;
            var ny = cy - rCentY * cs;
            probes++;
            var sc = EvaluateScore(sparsePoints!, preparedFloor, cs, nx, ny, viewportBounds, bestScore);
            if (sc > bestScore)
            {
                bestScore = sc;
                bS2 = cs;
                bX2 = nx;
                bY2 = ny;
            }
        }

        // Stage 3: Joint fine polish (3 scales x 7x7 translations)
        var finalX = bX2;
        var finalY = bY2;
        var finalS = bS2;
        var rCentX2 = (cx - bX2) / bS2;
        var rCentY2 = (cy - bY2) / bS2;

        for (var sIdx = 0; sIdx < ScaleFineDeltas.Length; sIdx++)
        {
            var fds = ScaleFineDeltas[sIdx];
            var cs = bS2 + fds;
            var nx = cx - rCentX2 * cs;
            var ny = cy - rCentY2 * cs;

            for (var xIdx = 0; xIdx < TranslationFineDeltas.Length; xIdx++)
            {
                var fdx = TranslationFineDeltas[xIdx];
                for (var yIdx = 0; yIdx < TranslationFineDeltas.Length; yIdx++)
                {
                    var fdy = TranslationFineDeltas[yIdx];
                    if (fds == 0.0d && fdx == 0.0d && fdy == 0.0d)
                        continue;

                    probes++;
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, cs, nx + fdx, ny + fdy, viewportBounds, bestScore);
                    if (sc > bestScore)
                    {
                        bestScore = sc;
                        finalS = cs;
                        finalX = nx + fdx;
                        finalY = ny + fdy;
                    }
                }
            }
        }

        // Stage 4: Subpixel 0.25px micro polish (8 probes)
        for (var sx = -1; sx <= 1; sx++)
        {
            for (var sy = -1; sy <= 1; sy++)
            {
                if (sx == 0 && sy == 0) continue;
                var subX = finalX + sx * 0.25d;
                var subY = finalY + sy * 0.25d;
                probes++;
                var sc = EvaluateScore(sparsePoints!, preparedFloor, finalS, subX, subY, viewportBounds, bestScore);
                if (sc > bestScore)
                {
                    bestScore = sc;
                    finalX = subX;
                    finalY = subY;
                }
            }
        }

        return (finalS, finalX, finalY, bestScore, probes);
    }

    /// <summary>
    /// Evaluates weighted potential field score.
    /// When K1 bitset is present: (3 * hits_K5 + 6 * hits_K3 + 1 * hits_K1) / (10 * N).
    /// Fallback without K1: (hits_K5 + 2 * hits_K3) / (3 * N).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateScore(
        IReadOnlyList<Point> sparsePoints,
        Vpsg3PreparedFloor preparedFloor,
        double scale,
        double offsetX,
        double offsetY,
        MapScreenRect viewportBounds)
        => EvaluateScore(sparsePoints, preparedFloor, scale, offsetX, offsetY, viewportBounds, -1d);

    private static double EvaluateScore(
        IReadOnlyList<Point> sparsePoints, Vpsg3PreparedFloor preparedFloor,
        double scale, double offsetX, double offsetY, MapScreenRect viewportBounds, double bestScore)
    {
        var hitsK5 = 0;
        var hitsK3 = 0;
        var hitsK1 = 0;
        var count = sparsePoints.Count;
        var invScale = 1.0d / scale;
        var points = sparsePoints as Point[];
        var k5 = preparedFloor.DilatedBitsetK5Span;
        var k3 = preparedFloor.DilatedBitsetK3Span;
        var k1 = preparedFloor.BitsetK1Span;
        if (k5.IsEmpty || k3.IsEmpty) return 0d;
        var width = preparedFloor.ReferenceWidth;
        var height = preparedFloor.ReferenceHeight;
        var wordsPerRow = preparedFloor.WordsPerRow;
        var hasK1 = !k1.IsEmpty;

        for (var i = 0; i < count; i++)
        {
            var q = points is not null ? points[i] : sparsePoints[i];
            var screenX = viewportBounds.X + q.X;
            var screenY = viewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - offsetX) * invScale);
            var ry = (int)Math.Round((screenY - offsetY) * invScale);

            if ((uint)rx < (uint)width && (uint)ry < (uint)height)
            {
                var index = ry * wordsPerRow + (rx >> 6);
                var shift = rx & 63;
                var word5 = k5[index];
                var hit5 = (int)((word5 >> shift) & 1UL);
                hitsK5 += hit5;
                if (hit5 != 0)
                {
                    var word3 = k3[index];
                    var hit3 = (int)((word3 >> shift) & 1UL);
                    hitsK3 += hit3;
                    if (hit3 != 0 && hasK1)
                    {
                        hitsK1 += (int)((k1[index] >> shift) & 1UL);
                    }
                }
            }

            if (hasK1)
            {
                if ((i & 15) == 15 &&
                    (3d * hitsK5 + 6d * hitsK3 + 1d * hitsK1 + 10d * (count - i - 1)) / (10d * count) <= bestScore)
                    return -1d;
            }
            else
            {
                if ((i & 15) == 15 &&
                    (hitsK5 + 2d * hitsK3 + 3d * (count - i - 1)) / (3d * count) <= bestScore)
                    return -1d;
            }
        }

        if (hasK1)
        {
            return (3.0d * hitsK5 + 6.0d * hitsK3 + 1.0d * hitsK1) / (10.0d * Math.Max(1, count));
        }
        return (hitsK5 + 2.0d * hitsK3) / (3.0d * Math.Max(1, count));
    }
}
