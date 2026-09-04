using System.Diagnostics;
using System.Numerics;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static partial class Vpsg3ScalePrototypes
{
    #region Helper Routines
    private static (double BestLag, double PeakRatio) FindDominantPitchNormalized(double[] signal)
    {
        var n = signal.Length;
        if (n < 24)
            return (0.0, 0.0);

        var mean = signal.Average();
        var centered = new double[n];
        var variance = 0.0;
        for (var i = 0; i < n; i++)
        {
            centered[i] = signal[i] - mean;
            variance += centered[i] * centered[i];
        }

        if (variance < Epsilon)
            return (0.0, 0.0);

        const int minLag = 12;
        var maxLag = n / 2;
        var bestLag = 0;
        var maxR = -1.0;
        var rValues = new List<double>();

        for (var lag = minLag; lag < maxLag; lag++)
        {
            var dot = 0.0;
            for (var i = 0; i < n - lag; i++)
                dot += centered[i] * centered[i + lag];

            var r = dot / variance;
            rValues.Add(r);

            if (r > maxR)
            {
                maxR = r;
                bestLag = lag;
            }
        }

        if (rValues.Count == 0 || maxR <= 0.05)
            return (0.0, 0.0);

        var sortedR = rValues.Select(Math.Abs).OrderBy(x => x).ToList();
        var medianR = sortedR[sortedR.Count / 2];
        var peakRatio = maxR / Math.Max(0.01, medianR);

        return (bestLag, peakRatio);
    }

    private static (ulong[] Words, int Width, int Height) BuildDownsampledBitset(Mat edgeMask, int factor)
    {
        var w = (edgeMask.Width + factor - 1) / factor;
        var h = (edgeMask.Height + factor - 1) / factor;
        var wordsPerRow = (w + 63) / 64;
        var words = new ulong[h * wordsPerRow];

        using var ds = new Mat();
        Cv2.Resize(edgeMask, ds, new Size(w, h), interpolation: InterpolationFlags.Nearest);

        for (var y = 0; y < h; y++)
        {
            var rowWordOffset = y * wordsPerRow;
            for (var x = 0; x < w; x++)
            {
                if (ds.At<byte>(y, x) > 128)
                {
                    var wordIdx = rowWordOffset + (x >> 6);
                    words[wordIdx] |= (1UL << (x & 63));
                }
            }
        }

        return (words, w, h);
    }

    private static double FastBitsetCorrelationAtScale(
        Mat queryEdges,
        ulong[] refBitset,
        int refW,
        int refH,
        double scale,
        int downsample)
    {
        var pts = SampleEdgePoints(queryEdges, maxPoints: 120);
        if (pts.Count == 0)
            return 0.0d;

        var qW = queryEdges.Width / downsample;
        var qH = queryEdges.Height / downsample;
        var wordsPerRow = (refW + 63) / 64;

        var maxHits = 0;
        const int stride = 2;

        for (var offY = 0; offY <= Math.Max(0, refH - (int)(qH / scale)); offY += stride)
        {
            for (var offX = 0; offX <= Math.Max(0, refW - (int)(qW / scale)); offX += stride)
            {
                var hits = 0;
                foreach (var p in pts)
                {
                    var rx = (int)Math.Round((p.X / scale) / downsample) + offX;
                    var ry = (int)Math.Round((p.Y / scale) / downsample) + offY;

                    if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
                    {
                        var wordIdx = ry * wordsPerRow + (rx >> 6);
                        if ((refBitset[wordIdx] & (1UL << (rx & 63))) != 0)
                            hits++;
                    }
                }

                if (hits > maxHits)
                    maxHits = hits;
            }
        }

        return (double)maxHits / pts.Count;
    }

    private static List<Point> SampleEdgePoints(Mat edges, int maxPoints)
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

        if (pts.Count <= maxPoints)
            return pts;

        var stepSize = (double)pts.Count / maxPoints;
        var result = new List<Point>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            result.Add(pts[(int)(i * stepSize)]);
        }
        return result;
    }

    private static double[] ComputeLogPairDistanceHistogram(
        List<Point> points,
        double minLog,
        double binWidth,
        int numBins)
    {
        var hist = new double[numBins];
        var n = points.Count;

        for (var i = 0; i < n; i++)
        {
            var pi = points[i];
            for (var j = i + 1; j < n; j++)
            {
                var pj = points[j];
                var dx = pi.X - pj.X;
                var dy = pi.Y - pj.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 <= 4)
                    continue;

                var logD = 0.5 * Math.Log(d2);
                var bin = (int)Math.Floor((logD - minLog) / binWidth);
                if (bin >= 0 && bin < numBins)
                    hist[bin]++;
            }
        }

        var sum = hist.Sum();
        if (sum > Epsilon)
        {
            for (var i = 0; i < numBins; i++)
                hist[i] /= sum;
        }

        return hist;
    }

    private static double[] Compute1DProjection(Mat edges, int axis)
    {
        using var reduce = new Mat();
        Cv2.Reduce(edges, reduce, (ReduceDimension)axis, ReduceTypes.Avg, MatType.CV_32F);
        var len = axis == 0 ? edges.Width : edges.Height;
        var result = new double[len];
        for (var i = 0; i < len; i++)
            result[i] = axis == 0 ? reduce.At<float>(0, i) : reduce.At<float>(i, 0);
        return result;
    }

    private static double GetQuantileDistance(Mat distMap, double quantile)
    {
        var vals = new List<float>();
        var w = distMap.Width;
        var h = distMap.Height;
        for (var y = 0; y < h; y += 3)
        {
            for (var x = 0; x < w; x += 3)
            {
                var v = distMap.At<float>(y, x);
                if (v > 1.0f)
                    vals.Add(v);
            }
        }

        if (vals.Count == 0)
            return 0.0;

        vals.Sort();
        var idx = (int)(vals.Count * quantile);
        return vals[Math.Clamp(idx, 0, vals.Count - 1)];
    }

    private static Point2d ComputeCentroid(List<Point> pts)
    {
        var sumX = pts.Sum(p => (double)p.X);
        var sumY = pts.Sum(p => (double)p.Y);
        return new Point2d(sumX / pts.Count, sumY / pts.Count);
    }

    private static double Distance(Point p, Point2d c)
    {
        var dx = p.X - c.X;
        var dy = p.Y - c.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double[] BuildHistogram(List<double> values, double min, double max, double binWidth)
    {
        var numBins = (int)Math.Ceiling((max - min) / binWidth);
        var hist = new double[numBins];
        foreach (var v in values)
        {
            var bin = (int)Math.Floor((v - min) / binWidth);
            if (bin >= 0 && bin < numBins)
                hist[bin]++;
        }
        var sum = hist.Sum();
        if (sum > Epsilon)
        {
            for (var i = 0; i < numBins; i++)
                hist[i] /= sum;
        }
        return hist;
    }

    private static double ComputeFwhm(List<(double LogS, double Score)> scores, double maxScore)
    {
        var half = maxScore * 0.5;
        var inHalf = scores.Where(s => s.Score >= half).ToList();
        if (inHalf.Count == 0)
            return 0.5;
        return inHalf.Max(s => s.LogS) - inHalf.Min(s => s.LogS);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    #endregion
}


