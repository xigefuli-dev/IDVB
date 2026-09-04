using System.Diagnostics;
using System.Numerics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static class Vpsg3TranslationPrototypes
{
    private const double Epsilon = 1e-6d;

    /// <summary>
    /// T-1: Single Token / Corner Hash.
    /// </summary>
    public static TranslationBenchmarkResult EvaluateTranslationMethodT1(
        Mat queryEdges,
        Mat refEdges,
        GroundTruthSample sample)
    {
        var sw = Stopwatch.StartNew();

        var queryCorners = ExtractHarrisCorners(queryEdges, maxCorners: 60);
        var refCorners = ExtractHarrisCorners(refEdges, maxCorners: 120);

        if (queryCorners.Count < 3 || refCorners.Count < 3)
        {
            sw.Stop();
            return new TranslationBenchmarkResult("T-1 (Single Corner Token)", sample.Id, 0, 0, sample.TrueOffsetX, sample.TrueOffsetY, 999.0, 0, 0, sw.Elapsed.TotalMilliseconds, false);
        }

        // Token descriptor: 8-neighborhood binary pattern around corner
        var qTokens = queryCorners.Select(c => (Corner: c, Token: ExtractCornerToken(queryEdges, c))).ToList();
        var rTokens = refCorners.Select(c => (Corner: c, Token: ExtractCornerToken(refEdges, c))).ToList();

        // Spatial voting accumulator for translation (dx, dy)
        var voteAccumulator = new Dictionary<(int Dx, int Dy), int>();
        var collisions = 0;

        foreach (var qt in qTokens)
        {
            var matchingRef = rTokens.Where(rt => rt.Token == qt.Token).ToList();
            if (matchingRef.Count > 1)
                collisions += (matchingRef.Count - 1);

            foreach (var rt in matchingRef)
            {
                // In reference space:
                // p_ref = (p_query / scale) + refOffset
                // => refOffset = rt.Corner - (qt.Corner / scale)
                var qScaledX = (int)Math.Round(qt.Corner.X / sample.TrueScale);
                var qScaledY = (int)Math.Round(qt.Corner.Y / sample.TrueScale);
                var dx = rt.Corner.X - qScaledX;
                var dy = rt.Corner.Y - qScaledY;

                // Quantize to 4px bin
                var bin = (dx / 4, dy / 4);
                voteAccumulator[bin] = voteAccumulator.GetValueOrDefault(bin, 0) + 1;
            }
        }

        var sortedVotes = voteAccumulator.OrderByDescending(kvp => kvp.Value).ToList();
        var bestVotes = sortedVotes.Count > 0 ? sortedVotes[0].Value : 0;
        var secondVotes = sortedVotes.Count > 1 ? sortedVotes[1].Value : 0;

        var bestDx = sortedVotes.Count > 0 ? sortedVotes[0].Key.Dx * 4 : 0;
        var bestDy = sortedVotes.Count > 0 ? sortedVotes[0].Key.Dy * 4 : 0;

        // Canonical offset conversion:
        // TrueOffsetX = ViewportBounds.X - cropX
        // Since bestDx represents refX - (queryX / scale) = cropX / scale:
        // cropX = bestDx * scale
        // => estimatedOffsetX = ViewportBounds.X - bestDx * scale
        var estOffsetX = sample.ViewportBounds.X - bestDx * sample.TrueScale;
        var estOffsetY = sample.ViewportBounds.Y - bestDy * sample.TrueScale;

        sw.Stop();

        var errX = estOffsetX - sample.TrueOffsetX;
        var errY = estOffsetY - sample.TrueOffsetY;
        var errDist = Math.Sqrt(errX * errX + errY * errY);

        var margin = (bestVotes - secondVotes) / (double)Math.Max(1, bestVotes);
        var survived = errDist <= 8.0;

        return new TranslationBenchmarkResult(
            "T-1 (Single Corner Token)",
            sample.Id,
            estOffsetX,
            estOffsetY,
            sample.TrueOffsetX,
            sample.TrueOffsetY,
            errDist,
            collisions,
            margin,
            sw.Elapsed.TotalMilliseconds,
            survived);
    }

    /// <summary>
    /// T-2: Rich Topology / Junction Hash.
    /// </summary>
    public static TranslationBenchmarkResult EvaluateTranslationMethodT2(
        Mat queryEdges,
        Mat refEdges,
        GroundTruthSample sample)
    {
        var sw = Stopwatch.StartNew();

        var qJunctions = ExtractJunctions(queryEdges);
        var rJunctions = ExtractJunctions(refEdges);

        if (qJunctions.Count < 2 || rJunctions.Count < 2)
        {
            sw.Stop();
            return new TranslationBenchmarkResult("T-2 (Rich Junction Hash)", sample.Id, 0, 0, sample.TrueOffsetX, sample.TrueOffsetY, 999.0, 0, 0, sw.Elapsed.TotalMilliseconds, false);
        }

        var voteMap = new Dictionary<(int Dx, int Dy), int>();
        var collisions = 0;

        foreach (var qj in qJunctions)
        {
            // Match junctions with same topological type (L, T, X) and branch count
            var matches = rJunctions.Where(rj => rj.Type == qj.Type && Math.Abs(rj.BranchCount - qj.BranchCount) == 0).ToList();
            if (matches.Count > 1)
                collisions += (matches.Count - 1);

            foreach (var rj in matches)
            {
                var qScaledX = (int)Math.Round(qj.Location.X / sample.TrueScale);
                var qScaledY = (int)Math.Round(qj.Location.Y / sample.TrueScale);
                var dx = rj.Location.X - qScaledX;
                var dy = rj.Location.Y - qScaledY;

                var bin = (dx / 4, dy / 4);
                voteMap[bin] = voteMap.GetValueOrDefault(bin, 0) + 1;
            }
        }

        var sorted = voteMap.OrderByDescending(kvp => kvp.Value).ToList();
        var bestVotes = sorted.Count > 0 ? sorted[0].Value : 0;
        var secondVotes = sorted.Count > 1 ? sorted[1].Value : 0;

        var bestDx = sorted.Count > 0 ? sorted[0].Key.Dx * 4 : 0;
        var bestDy = sorted.Count > 0 ? sorted[0].Key.Dy * 4 : 0;

        var estOffsetX = sample.ViewportBounds.X - bestDx * sample.TrueScale;
        var estOffsetY = sample.ViewportBounds.Y - bestDy * sample.TrueScale;

        sw.Stop();

        var errX = estOffsetX - sample.TrueOffsetX;
        var errY = estOffsetY - sample.TrueOffsetY;
        var errDist = Math.Sqrt(errX * errX + errY * errY);

        var margin = (bestVotes - secondVotes) / (double)Math.Max(1, bestVotes);
        var survived = errDist <= 8.0;

        return new TranslationBenchmarkResult(
            "T-2 (Rich Junction Hash)",
            sample.Id,
            estOffsetX,
            estOffsetY,
            sample.TrueOffsetX,
            sample.TrueOffsetY,
            errDist,
            collisions,
            margin,
            sw.Elapsed.TotalMilliseconds,
            survived);
    }

    /// <summary>
    /// T-3: Anchor Constellation / 2D Bitset Correlation.
    /// </summary>
    public static TranslationBenchmarkResult EvaluateTranslationMethodT3(
        Mat queryEdges,
        Mat refEdges,
        GroundTruthSample sample)
    {
        var sw = Stopwatch.StartNew();

        // 1. Dilate reference slightly (1px) for matching tolerance
        using var refDilated = new Mat();
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(refEdges, refDilated, k3);

        // 2. Build 64-bit word bitset for reference
        var refW = refDilated.Width;
        var refH = refDilated.Height;
        var wordsPerRow = (refW + 63) / 64;
        var refWords = new ulong[refH * wordsPerRow];

        for (var y = 0; y < refH; y++)
        {
            var wordOff = y * wordsPerRow;
            for (var x = 0; x < refW; x++)
            {
                if (refDilated.At<byte>(y, x) > 128)
                    refWords[wordOff + (x >> 6)] |= (1UL << (x & 63));
            }
        }

        // 3. Sample query points (constellation of 150 points)
        var queryPts = SamplePoints(queryEdges, maxPts: 150);
        if (queryPts.Count == 0)
        {
            sw.Stop();
            return new TranslationBenchmarkResult("T-3 (Bitset Constellation)", sample.Id, 0, 0, sample.TrueOffsetX, sample.TrueOffsetY, 999.0, 0, 0, sw.Elapsed.TotalMilliseconds, false);
        }

        // Scale query points to reference scale
        var scaledPts = queryPts.Select(p => new Point(
            (int)Math.Round(p.X / sample.TrueScale),
            (int)Math.Round(p.Y / sample.TrueScale))).ToList();

        // Search coarse 2D translation grid with 4px stride
        const int stride = 4;
        var bestDx = 0;
        var bestDy = 0;
        var bestHits = 0;
        var secondHits = 0;
        var collisionCount = 0;

        var minDx = 0;
        var maxDx = Math.Max(0, refW - (int)(sample.QueryBounds.Width / sample.TrueScale));
        var minDy = 0;
        var maxDy = Math.Max(0, refH - (int)(sample.QueryBounds.Height / sample.TrueScale));

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = 0;
                foreach (var pt in scaledPts)
                {
                    var rx = pt.X + dx;
                    var ry = pt.Y + dy;
                    if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
                    {
                        var wordIdx = ry * wordsPerRow + (rx >> 6);
                        if ((refWords[wordIdx] & (1UL << (rx & 63))) != 0)
                            hits++;
                    }
                }

                if (hits > bestHits)
                {
                    secondHits = bestHits;
                    bestHits = hits;
                    bestDx = dx;
                    bestDy = dy;
                }
                else if (hits == bestHits && hits > 10)
                {
                    collisionCount++;
                }
                else if (hits > secondHits)
                {
                    secondHits = hits;
                }
            }
        }

        // Canonical translation calculation:
        // dx = cropX / scale => cropX = dx * scale
        // TrueOffsetX = ViewportBounds.X - cropX = ViewportBounds.X - bestDx * scale
        var estOffsetX = sample.ViewportBounds.X - bestDx * sample.TrueScale;
        var estOffsetY = sample.ViewportBounds.Y - bestDy * sample.TrueScale;

        sw.Stop();

        var errX = estOffsetX - sample.TrueOffsetX;
        var errY = estOffsetY - sample.TrueOffsetY;
        var errDist = Math.Sqrt(errX * errX + errY * errY);

        var margin = (bestHits - secondHits) / (double)Math.Max(1, bestHits);
        var survived = errDist <= 6.0;

        return new TranslationBenchmarkResult(
            "T-3 (Bitset Constellation)",
            sample.Id,
            estOffsetX,
            estOffsetY,
            sample.TrueOffsetX,
            sample.TrueOffsetY,
            errDist,
            collisionCount,
            margin,
            sw.Elapsed.TotalMilliseconds,
            survived);
    }

    public static List<(double OffsetX, double OffsetY, int Score)> GenerateT3Candidates(
        Mat queryEdges,
        Mat refEdges,
        GroundTruthSample sample,
        double? estimatedScale = null,
        int topK = 8)
    {
        var scale = estimatedScale ?? sample.TrueScale;
        using var refDilated = new Mat();
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(refEdges, refDilated, k3);

        var refW = refDilated.Width;
        var refH = refDilated.Height;
        var wordsPerRow = (refW + 63) / 64;
        var refWords = new ulong[refH * wordsPerRow];

        for (var y = 0; y < refH; y++)
        {
            var wordOff = y * wordsPerRow;
            for (var x = 0; x < refW; x++)
            {
                if (refDilated.At<byte>(y, x) > 128)
                    refWords[wordOff + (x >> 6)] |= (1UL << (x & 63));
            }
        }

        var queryPts = SamplePoints(queryEdges, maxPts: 150);
        if (queryPts.Count == 0)
            return new List<(double OffsetX, double OffsetY, int Score)>();

        var scaledPts = queryPts.Select(p => new Point(
            (int)Math.Round(p.X / scale),
            (int)Math.Round(p.Y / scale))).ToList();

        const int stride = 4;
        var minDx = 0;
        var maxDx = Math.Max(0, refW - (int)(sample.QueryBounds.Width / scale));
        var minDy = 0;
        var maxDy = Math.Max(0, refH - (int)(sample.QueryBounds.Height / scale));

        var candidates = new List<(int Dx, int Dy, int Score)>();

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = 0;
                foreach (var pt in scaledPts)
                {
                    var rx = pt.X + dx;
                    var ry = pt.Y + dy;
                    if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
                    {
                        var wordIdx = ry * wordsPerRow + (rx >> 6);
                        if ((refWords[wordIdx] & (1UL << (rx & 63))) != 0)
                            hits++;
                    }
                }

                if (hits > 5)
                    candidates.Add((dx, dy, hits));
            }
        }

        var topCands = candidates.OrderByDescending(c => c.Score).Take(topK).ToList();
        var refined = new List<(double OffsetX, double OffsetY, int Score)>();

        foreach (var (cdx, cdy, cscore) in topCands)
        {
            var bestSubDx = cdx;
            var bestSubDy = cdy;
            var bestSubScore = cscore;

            for (var ldy = Math.Max(0, cdy - 2); ldy <= Math.Min(maxDy, cdy + 2); ldy++)
            {
                for (var ldx = Math.Max(0, cdx - 2); ldx <= Math.Min(maxDx, cdx + 2); ldx++)
                {
                    var subHits = 0;
                    foreach (var pt in scaledPts)
                    {
                        var rx = pt.X + ldx;
                        var ry = pt.Y + ldy;
                        if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
                        {
                            var wordIdx = ry * wordsPerRow + (rx >> 6);
                            if ((refWords[wordIdx] & (1UL << (rx & 63))) != 0)
                                subHits++;
                        }
                    }
                    if (subHits > bestSubScore)
                    {
                        bestSubScore = subHits;
                        bestSubDx = ldx;
                        bestSubDy = ldy;
                    }
                }
            }

            refined.Add((
                sample.ViewportBounds.X - bestSubDx * scale,
                sample.ViewportBounds.Y - bestSubDy * scale,
                bestSubScore));
        }

        return refined;
    }

    /// <summary>
    /// Evaluates T-3 as a Top-K candidate generator.
    /// </summary>
    public static TranslationTopKResult EvaluateTranslationTopK(
        Mat queryEdges,
        Mat refEdges,
        GroundTruthSample sample,
        double? estimatedScale = null,
        int topK = 8)
    {
        var sw = Stopwatch.StartNew();
        var candidates = GenerateT3Candidates(queryEdges, refEdges, sample, estimatedScale, topK);
        sw.Stop();

        if (candidates.Count == 0)
        {
            return new TranslationTopKResult("T-3 (Bitset Constellation)", sample.Id, sample.SourceType, 999.0, false, false, false, false, false, false, 999.0, 0, sw.Elapsed.TotalMilliseconds);
        }

        var scoredErrors = candidates.Select(c =>
            Math.Sqrt(Math.Pow(c.OffsetX - sample.TrueOffsetX, 2) + Math.Pow(c.OffsetY - sample.TrueOffsetY, 2))
        ).ToList();

        var top1Err = scoredErrors[0];
        var top1Hit2px = top1Err <= 2.0d;
        var top1Hit3px = top1Err <= 3.0d;
        var top1Hit5px = top1Err <= 5.0d;

        var top2Recall = scoredErrors.Take(2).Any(e => e <= 3.0d);
        var top4Recall = scoredErrors.Take(4).Any(e => e <= 3.0d);
        var top8Recall = scoredErrors.Take(8).Any(e => e <= 3.0d);
        var bestErr = scoredErrors.Min();

        var top1Score = candidates[0].Score;
        var top2Score = candidates.Count > 1 ? candidates[1].Score : 0;
        var margin = (top1Score - top2Score) / (double)Math.Max(1, top1Score);

        return new TranslationTopKResult(
            "T-3 (Bitset Constellation)",
            sample.Id,
            sample.SourceType,
            top1Err,
            top1Hit2px,
            top1Hit3px,
            top1Hit5px,
            top2Recall,
            top4Recall,
            top8Recall,
            bestErr,
            margin,
            sw.Elapsed.TotalMilliseconds);
    }

    #region Helpers

    private static List<Point> ExtractHarrisCorners(Mat edgeImage, int maxCorners)
    {
        using var corners = new Mat();
        Cv2.CornerHarris(edgeImage, corners, blockSize: 3, ksize: 3, k: 0.04);
        Cv2.Normalize(corners, corners, 0, 255, NormTypes.MinMax);

        var list = new List<(Point Pt, float Val)>();
        var w = corners.Width;
        var h = corners.Height;
        for (var y = 3; y < h - 3; y += 2)
        {
            for (var x = 3; x < w - 3; x += 2)
            {
                var val = corners.At<float>(y, x);
                if (val > 80.0f)
                    list.Add((new Point(x, y), val));
            }
        }

        return list.OrderByDescending(p => p.Val).Take(maxCorners).Select(p => p.Pt).ToList();
    }

    private static int ExtractCornerToken(Mat image, Point pt)
    {
        // Sample 8-neighborhood at radius 4
        var token = 0;
        var w = image.Width;
        var h = image.Height;
        var angles = new[] { 0, 45, 90, 135, 180, 225, 270, 315 };
        for (var i = 0; i < 8; i++)
        {
            var rad = angles[i] * Math.PI / 180.0;
            var nx = pt.X + (int)Math.Round(4.0 * Math.Cos(rad));
            var ny = pt.Y + (int)Math.Round(4.0 * Math.Sin(rad));
            if (nx >= 0 && nx < w && ny >= 0 && ny < h)
            {
                if (image.At<byte>(ny, nx) > 128)
                    token |= (1 << i);
            }
        }
        return token;
    }

    private sealed record Junction(Point Location, string Type, int BranchCount);

    private static List<Junction> ExtractJunctions(Mat edgeImage)
    {
        var junctions = new List<Junction>();
        var corners = ExtractHarrisCorners(edgeImage, 80);

        foreach (var c in corners)
        {
            var token = ExtractCornerToken(edgeImage, c);
            var branchCount = BitOperations.PopCount((uint)token);
            var type = branchCount switch
            {
                2 => "L-Corner",
                3 => "T-Junction",
                >= 4 => "X-Crossing",
                _ => "End-Point"
            };

            if (branchCount >= 2)
            {
                junctions.Add(new Junction(c, type, branchCount));
            }
        }

        return junctions;
    }

    private static List<Point> SamplePoints(Mat edges, int maxPts)
    {
        var pts = new List<Point>();
        var w = edges.Width;
        var h = edges.Height;

        for (var y = 2; y < h - 2; y += 3)
        {
            for (var x = 2; x < w - 2; x += 3)
            {
                if (edges.At<byte>(y, x) > 128)
                    pts.Add(new Point(x, y));
            }
        }

        if (pts.Count <= maxPts)
            return pts;

        var stepSize = (double)pts.Count / maxPts;
        var result = new List<Point>(maxPts);
        for (var i = 0; i < maxPts; i++)
            result.Add(pts[(int)(i * stepSize)]);
        return result;
    }

    #endregion
}
