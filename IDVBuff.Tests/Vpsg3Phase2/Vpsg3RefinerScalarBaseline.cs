using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production local refiner for VPSG 3.0.
/// Employs hierarchical separable refinement (57 probes) with convex weighted potential field
/// scoring: (hits_K5 + 2 * hits_K3) / (3 * N).
/// Centered scaling parameterization preserves canonical transform coordinate semantics.
/// </summary>
internal static class Vpsg3RefinerScalarBaseline
{
    private static readonly double[] ScaleCoarseDeltas = [-0.020d, -0.015d, 0.000d, 0.015d, 0.020d];
    private static readonly double[] TranslationCoarseDeltas = [-6.0d, -4.0d, -2.0d, 0.0d, 2.0d, 4.0d, 6.0d];
    private static readonly double[] ScaleScanDeltas = [-0.020d, -0.010d, -0.005d, 0.005d, 0.010d, 0.020d];
    private static readonly double[] TranslationFineDeltas = [-1.5d, 0.0d, 1.5d];
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
        int height)
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

        // Stage 1: Coarse Joint Scale-Translation Grid (3 scales x 5x5 translations = 75 probes)
        var bS = seedScale;
        var bX = seedX;
        var bY = seedY;
        var bestScore = EvaluateScore(sparsePoints!, preparedFloor, seedScale, seedX, seedY, viewportBounds);

        for (var sIdx = 0; sIdx < ScaleCoarseDeltas.Length; sIdx++)
        {
            var ds = ScaleCoarseDeltas[sIdx];
            var cs = seedScale + ds;
            if (cs < 0.65d || cs > 1.60d) continue;

            var bx = cx - rcx * cs;
            var by = cy - rcy * cs;

            for (var i = 0; i < TranslationCoarseDeltas.Length; i++)
            {
                var dx = TranslationCoarseDeltas[i];
                for (var j = 0; j < TranslationCoarseDeltas.Length; j++)
                {
                    var dy = TranslationCoarseDeltas[j];
                    probes++;
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, cs, bx + dx, by + dy, viewportBounds);
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
            if (cs < 0.65d || cs > 1.60d) continue;

            var nx = cx - rCentX * cs;
            var ny = cy - rCentY * cs;
            probes++;
            var sc = EvaluateScore(sparsePoints!, preparedFloor, cs, nx, ny, viewportBounds);
            if (sc > bestScore)
            {
                bestScore = sc;
                bS2 = cs;
                bX2 = nx;
                bY2 = ny;
            }
        }

        // Stage 3: Joint fine polish (3 scales x 3x3 translations)
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
                    var sc = EvaluateScore(sparsePoints!, preparedFloor, cs, nx + fdx, ny + fdy, viewportBounds);
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

        return (finalS, finalX, finalY, bestScore, probes);
    }

    /// <summary>
    /// Evaluates weighted potential field score: (hits_K5 + 2 * hits_K3) / (3 * N).
    /// Uses dual bitset simultaneous check without heap allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateScore(
        IReadOnlyList<Point> sparsePoints,
        Vpsg3PreparedFloor preparedFloor,
        double scale,
        double offsetX,
        double offsetY,
        MapScreenRect viewportBounds)
    {
        var hitsK5 = 0;
        var hitsK3 = 0;
        var count = sparsePoints.Count;
        var invScale = 1.0d / scale;

        for (var i = 0; i < count; i++)
        {
            var q = sparsePoints[i];
            var screenX = viewportBounds.X + q.X;
            var screenY = viewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - offsetX) * invScale);
            var ry = (int)Math.Round((screenY - offsetY) * invScale);

            preparedFloor.TestK3K5(rx, ry, out var isK5, out var isK3);
            if (isK5)
            {
                hitsK5++;
                if (isK3) hitsK3++;
            }
        }

        return (hitsK5 + 2.0d * hitsK3) / (3.0d * Math.Max(1, count));
    }
}

