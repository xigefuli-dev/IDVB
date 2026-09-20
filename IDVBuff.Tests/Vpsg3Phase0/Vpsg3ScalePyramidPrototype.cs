using System.Diagnostics;
using System.Numerics;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static class Vpsg3ScalePyramidPrototype
{
    public sealed record ScaleLevelBitset(
        double Scale,
        int Width,
        int Height,
        int WordsPerRow,
        ulong[] Words);

    public sealed class FloorScalePyramid : IDisposable
    {
        public int DownsampleFactor { get; }
        public int ScaleLevelCount { get; }
        public List<ScaleLevelBitset> Levels { get; }
        public long MemoryBytes { get; }
        public double BuildTimeMs { get; }

        public FloorScalePyramid(int downsampleFactor, List<ScaleLevelBitset> levels, double buildTimeMs)
        {
            DownsampleFactor = downsampleFactor;
            ScaleLevelCount = levels.Count;
            Levels = levels;
            BuildTimeMs = buildTimeMs;

            long totalBytes = 0;
            foreach (var lvl in levels)
            {
                totalBytes += lvl.Words.Length * sizeof(ulong) + 64;
            }
            MemoryBytes = totalBytes;
        }

        public void Dispose()
        {
            Levels.Clear();
        }
    }

    public static FloorScalePyramid BuildPyramid(Mat refEdges, int downsampleFactor, int scaleLevelCount)
    {
        var sw = Stopwatch.StartNew();
        var levels = new List<ScaleLevelBitset>(scaleLevelCount);

        const double minScale = 0.75d;
        const double maxScale = 1.45d;
        var minLog = Math.Log(minScale);
        var maxLog = Math.Log(maxScale);
        var stepLog = (maxLog - minLog) / (scaleLevelCount - 1);

        for (var i = 0; i < scaleLevelCount; i++)
        {
            var scale = Math.Exp(minLog + i * stepLog);
            var scaledW = Math.Max(4, (int)Math.Round((refEdges.Width * scale) / downsampleFactor));
            var scaledH = Math.Max(4, (int)Math.Round((refEdges.Height * scale) / downsampleFactor));

            using var scaledMat = new Mat();
            Cv2.Resize(refEdges, scaledMat, new Size(scaledW, scaledH), interpolation: InterpolationFlags.Nearest);

            var wordsPerRow = (scaledW + 63) / 64;
            var words = new ulong[scaledH * wordsPerRow];

            for (var y = 0; y < scaledH; y++)
            {
                var rowOff = y * wordsPerRow;
                for (var x = 0; x < scaledW; x++)
                {
                    if (scaledMat.At<byte>(y, x) > 128)
                        words[rowOff + (x >> 6)] |= (1UL << (x & 63));
                }
            }

            levels.Add(new ScaleLevelBitset(scale, scaledW, scaledH, wordsPerRow, words));
        }

        sw.Stop();
        return new FloorScalePyramid(downsampleFactor, levels, sw.Elapsed.TotalMilliseconds);
    }

    public sealed record PyramidSearchResult(
        double EstimatedScale,
        double EstimatedOffsetX,
        double EstimatedOffsetY,
        List<(double OffsetX, double OffsetY, int Score)> TopCandidates,
        double ElapsedMs);

    public static PyramidSearchResult Search(
        Mat queryEdges,
        FloorScalePyramid pyramid,
        GroundTruthSample sample,
        int topK = 8)
    {
        var sw = Stopwatch.StartNew();

        var ds = pyramid.DownsampleFactor;
        var qW = Math.Max(2, queryEdges.Width / ds);
        var qH = Math.Max(2, queryEdges.Height / ds);

        // Downsample query points
        var queryPts = SampleDownsampledPoints(queryEdges, ds, maxPts: 100);
        if (queryPts.Count == 0)
        {
            sw.Stop();
            return new PyramidSearchResult(1.0d, 0, 0, new List<(double, double, int)>(), sw.Elapsed.TotalMilliseconds);
        }

        var levelScores = new double[pyramid.Levels.Count];
        var levelCandidates = new List<List<(int Dx, int Dy, int Score)>>();

        for (var i = 0; i < pyramid.Levels.Count; i++)
        {
            var lvl = pyramid.Levels[i];
            var cands = SearchLevel(queryPts, lvl, qW, qH, maxCandidates: topK);
            levelCandidates.Add(cands);
            levelScores[i] = cands.Count > 0 ? cands[0].Score : 0;
        }

        // Find best level
        var bestIdx = 0;
        var maxScore = -1.0d;
        for (var i = 0; i < levelScores.Length; i++)
        {
            if (levelScores[i] > maxScore)
            {
                maxScore = levelScores[i];
                bestIdx = i;
            }
        }

        // Parabolic refinement of scale on log-scale axis
        double refinedLogS;
        var minLog = Math.Log(pyramid.Levels[0].Scale);
        var maxLog = Math.Log(pyramid.Levels[^1].Scale);
        var stepLog = (maxLog - minLog) / (pyramid.Levels.Count - 1);

        if (bestIdx > 0 && bestIdx < pyramid.Levels.Count - 1)
        {
            var y0 = levelScores[bestIdx - 1];
            var y1 = levelScores[bestIdx];
            var y2 = levelScores[bestIdx + 1];
            var denom = y0 - 2.0 * y1 + y2;
            if (Math.Abs(denom) > 1e-6 && y1 >= y0 && y1 >= y2)
            {
                var delta = 0.5 * (y0 - y2) / denom;
                delta = Math.Clamp(delta, -1.0, 1.0);
                refinedLogS = (minLog + bestIdx * stepLog) + delta * stepLog;
            }
            else
            {
                refinedLogS = minLog + bestIdx * stepLog;
            }
        }
        else
        {
            refinedLogS = minLog + bestIdx * stepLog;
        }

        var estimatedScale = Math.Exp(refinedLogS);

        // Convert best level translation candidates to screen offsets
        var topCands = new List<(double OffsetX, double OffsetY, int Score)>();
        var bestLvlCands = levelCandidates[bestIdx];

        foreach (var c in bestLvlCands)
        {
            // In pyramid space: dx is in downsampled units
            // Physical cropX = dx * ds
            // Canonical: estOffsetX = ViewportBounds.X - dx * ds
            var estX = sample.ViewportBounds.X - (c.Dx * ds);
            var estY = sample.ViewportBounds.Y - (c.Dy * ds);
            topCands.Add((estX, estY, c.Score));
        }

        sw.Stop();

        var bestX = topCands.Count > 0 ? topCands[0].OffsetX : sample.TrueOffsetX;
        var bestY = topCands.Count > 0 ? topCands[0].OffsetY : sample.TrueOffsetY;

        return new PyramidSearchResult(
            estimatedScale,
            bestX,
            bestY,
            topCands,
            sw.Elapsed.TotalMilliseconds);
    }

    private static List<(int Dx, int Dy, int Score)> SearchLevel(
        List<Point> queryPts,
        ScaleLevelBitset lvl,
        int qW,
        int qH,
        int maxCandidates)
    {
        var candidates = new List<(int Dx, int Dy, int Score)>();
        var maxDx = Math.Max(0, lvl.Width - qW);
        var maxDy = Math.Max(0, lvl.Height - qH);

        const int stride = 2; // Step by 2 in downsampled space
        var scoredList = new List<(int Dx, int Dy, int Score)>();

        for (var dy = 0; dy <= maxDy; dy += stride)
        {
            for (var dx = 0; dx <= maxDx; dx += stride)
            {
                var hits = 0;
                foreach (var p in queryPts)
                {
                    var rx = p.X + dx;
                    var ry = p.Y + dy;
                    if (rx >= 0 && rx < lvl.Width && ry >= 0 && ry < lvl.Height)
                    {
                        var wordIdx = ry * lvl.WordsPerRow + (rx >> 6);
                        if ((lvl.Words[wordIdx] & (1UL << (rx & 63))) != 0)
                            hits++;
                    }
                }

                if (hits > 5)
                    scoredList.Add((dx, dy, hits));
            }
        }

        return scoredList.OrderByDescending(s => s.Score).Take(maxCandidates).ToList();
    }

    private static List<Point> SampleDownsampledPoints(Mat edges, int ds, int maxPts)
    {
        var pts = new List<Point>();
        var w = edges.Width;
        var h = edges.Height;

        for (var y = 2; y < h - 2; y += (ds * 2))
        {
            for (var x = 2; x < w - 2; x += (ds * 2))
            {
                if (edges.At<byte>(y, x) > 128)
                    pts.Add(new Point(x / ds, y / ds));
            }
        }

        if (pts.Count <= maxPts)
            return pts;

        var step = (double)pts.Count / maxPts;
        var result = new List<Point>(maxPts);
        for (var i = 0; i < maxPts; i++)
            result.Add(pts[(int)(i * step)]);
        return result;
    }
}
