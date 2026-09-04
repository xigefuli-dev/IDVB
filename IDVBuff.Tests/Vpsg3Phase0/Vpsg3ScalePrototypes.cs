using System.Diagnostics;
using System.Numerics;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static class Vpsg3ScalePrototypes
{
    private const double Epsilon = 1e-6d;
    public const double DomainMinScale = 0.70d;
    public const double DomainMaxScale = 1.50d;

    /// <summary>
    /// S-A: Log Pair-Distance Histogram Correlation.
    /// </summary>
    public static ScaleBenchmarkResult EvaluateScaleMethodA(
        Mat queryEdges,
        Mat refEdges,
        double trueScale,
        string sampleId,
        string sourceType)
    {
        var sw = Stopwatch.StartNew();

        var queryPts = SampleEdgePoints(queryEdges, maxPoints: 200);
        var refPts = SampleEdgePoints(refEdges, maxPoints: 300);

        if (queryPts.Count < 10 || refPts.Count < 10)
        {
            sw.Stop();
            return new ScaleBenchmarkResult("S-A (Log Pair-Dist)", sampleId, sourceType, 1.0, trueScale, Math.Abs(1.0 - trueScale), 0, 1.0, sw.Elapsed.TotalMilliseconds, false);
        }

        const double minLog = 1.0d;
        const double maxLog = 7.5d;
        const double binWidth = 0.02d;
        var numBins = (int)Math.Ceiling((maxLog - minLog) / binWidth);

        var queryHist = ComputeLogPairDistanceHistogram(queryPts, minLog, binWidth, numBins);
        var refHist = ComputeLogPairDistanceHistogram(refPts, minLog, binWidth, numBins);

        var minShift = (int)Math.Floor(Math.Log(DomainMinScale) / binWidth);
        var maxShift = (int)Math.Ceiling(Math.Log(DomainMaxScale) / binWidth);

        var bestCorr = -1.0d;
        var secondCorr = -1.0d;
        var bestShift = 0;
        var corrScores = new List<(double LogS, double Score)>();

        for (var shift = minShift; shift <= maxShift; shift++)
        {
            var dot = 0.0d;
            var normQ = 0.0d;
            var normR = 0.0d;

            for (var i = 0; i < numBins; i++)
            {
                var j = i + shift;
                if (j >= 0 && j < numBins)
                {
                    var q = queryHist[i];
                    var r = refHist[j];
                    dot += q * r;
                    normQ += q * q;
                    normR += r * r;
                }
            }

            var score = (normQ > Epsilon && normR > Epsilon) ? (dot / Math.Sqrt(normQ * normR)) : 0.0d;
            var logS = -shift * binWidth;
            corrScores.Add((logS, score));

            if (score > bestCorr)
            {
                secondCorr = bestCorr;
                bestCorr = score;
                bestShift = shift;
            }
            else if (score > secondCorr)
            {
                secondCorr = score;
            }
        }

        var estimatedLogS = bestShift * binWidth;
        var estimatedScale = Math.Clamp(Math.Exp(estimatedLogS), DomainMinScale, DomainMaxScale);

        sw.Stop();

        var margin = (bestCorr - secondCorr) / (bestCorr + Epsilon);
        var fwhm = ComputeFwhm(corrScores, bestCorr);
        var error = Math.Abs(estimatedScale - trueScale);
        var success = error < 0.08d && margin > 0.02d;

        return new ScaleBenchmarkResult(
            "S-A (Log Pair-Dist)",
            sampleId,
            sourceType,
            estimatedScale,
            trueScale,
            error,
            margin,
            fwhm,
            sw.Elapsed.TotalMilliseconds,
            success);
    }

    /// <summary>
    /// S-B: Wall Spacing / Axis-Projection Normalized Autocorrelation.
    /// </summary>
    public static (ScaleBenchmarkResult Result, double PeakRatio) EvaluateScaleMethodB(
        Mat queryEdges,
        Mat refEdges,
        double trueScale,
        string sampleId,
        string sourceType)
    {
        var sw = Stopwatch.StartNew();

        var qProjX = Compute1DProjection(queryEdges, axis: 0);
        var rProjX = Compute1DProjection(refEdges, axis: 0);

        var (qPitch, qRatio) = FindDominantPitchNormalized(qProjX);
        var (rPitch, rRatio) = FindDominantPitchNormalized(rProjX);

        sw.Stop();

        double estimatedScale;
        double peakRatio = Math.Min(qRatio, rRatio);

        if (rPitch > 5.0 && qPitch > 5.0)
        {
            var rawScale = qPitch / rPitch;
            // Check if raw scale is physically plausible inside the domain
            if (rawScale >= DomainMinScale && rawScale <= DomainMaxScale)
            {
                estimatedScale = rawScale;
            }
            else
            {
                // Incompatible harmonic intervals; clamp and mark low ratio
                estimatedScale = Math.Clamp(rawScale, DomainMinScale, DomainMaxScale);
                peakRatio = 1.0;
            }
        }
        else
        {
            estimatedScale = 1.0d;
            peakRatio = 1.0;
        }

        var error = Math.Abs(estimatedScale - trueScale);
        var success = error < 0.05d;

        var result = new ScaleBenchmarkResult(
            "S-B (Wall Spacing)",
            sampleId,
            sourceType,
            estimatedScale,
            trueScale,
            error,
            NormalizedMargin: Math.Min(1.0, peakRatio / 5.0),
            FwhmLogScale: 0.35d,
            sw.Elapsed.TotalMilliseconds,
            Success: success);

        return (result, peakRatio);
    }

    /// <summary>
    /// S-C: Nearest Distance Spectrum.
    /// </summary>
    public static ScaleBenchmarkResult EvaluateScaleMethodC(
        Mat queryEdges,
        Mat refEdges,
        double trueScale,
        string sampleId,
        string sourceType)
    {
        var sw = Stopwatch.StartNew();

        using var qDist = new Mat();
        using var rDist = new Mat();
        using var qInv = new Mat();
        using var rInv = new Mat();

        Cv2.BitwiseNot(queryEdges, qInv);
        Cv2.BitwiseNot(refEdges, rInv);

        Cv2.DistanceTransform(qInv, qDist, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        Cv2.DistanceTransform(rInv, rDist, DistanceTypes.L2, DistanceTransformMasks.Mask5);

        var qMed = GetQuantileDistance(qDist, quantile: 0.70);
        var rMed = GetQuantileDistance(rDist, quantile: 0.70);

        sw.Stop();

        var rawScale = (rMed > 2.0 && qMed > 2.0) ? (qMed / rMed) : 1.0d;
        var estimatedScale = Math.Clamp(rawScale, DomainMinScale, DomainMaxScale);
        var error = Math.Abs(estimatedScale - trueScale);

        return new ScaleBenchmarkResult(
            "S-C (Nearest Dist Spectrum)",
            sampleId,
            sourceType,
            estimatedScale,
            trueScale,
            error,
            NormalizedMargin: 0.03d,
            FwhmLogScale: 0.40d,
            sw.Elapsed.TotalMilliseconds,
            Success: error < 0.10d);
    }

    /// <summary>
    /// S-D: 1D Radial Projection Correlation from Centroid.
    /// </summary>
    public static ScaleBenchmarkResult EvaluateScaleMethodD(
        Mat queryEdges,
        Mat refEdges,
        double trueScale,
        string sampleId,
        string sourceType)
    {
        var sw = Stopwatch.StartNew();

        var qPts = SampleEdgePoints(queryEdges, maxPoints: 250);
        var rPts = SampleEdgePoints(refEdges, maxPoints: 400);

        if (qPts.Count < 10 || rPts.Count < 10)
        {
            sw.Stop();
            return new ScaleBenchmarkResult("S-D (Radial Projection)", sampleId, sourceType, 1.0, trueScale, Math.Abs(1.0 - trueScale), 0, 1.0, sw.Elapsed.TotalMilliseconds, false);
        }

        var qCentroid = ComputeCentroid(qPts);
        var rCentroid = ComputeCentroid(rPts);

        var qLogR = qPts.Select(p => Math.Log(Math.Max(1.0, Distance(p, qCentroid)))).ToList();
        var rLogR = rPts.Select(p => Math.Log(Math.Max(1.0, Distance(p, rCentroid)))).ToList();

        const double binW = 0.03d;
        var qHist = BuildHistogram(qLogR, 0.0, 7.5, binW);
        var rHist = BuildHistogram(rLogR, 0.0, 7.5, binW);

        var bestShift = 0;
        var bestDot = -1.0;
        var secondDot = -1.0;
        var minShift = (int)Math.Floor(Math.Log(DomainMinScale) / binW);
        var maxShift = (int)Math.Ceiling(Math.Log(DomainMaxScale) / binW);

        for (var s = minShift; s <= maxShift; s++)
        {
            var dot = 0.0;
            for (var i = 0; i < qHist.Length; i++)
            {
                var j = i + s;
                if (j >= 0 && j < rHist.Length)
                    dot += qHist[i] * rHist[j];
            }
            if (dot > bestDot)
            {
                secondDot = bestDot;
                bestDot = dot;
                bestShift = s;
            }
            else if (dot > secondDot)
            {
                secondDot = dot;
            }
        }

        var rawScale = Math.Exp(bestShift * binW);
        var estimatedScale = Math.Clamp(rawScale, DomainMinScale, DomainMaxScale);
        sw.Stop();

        var margin = (bestDot - secondDot) / (bestDot + Epsilon);
        var error = Math.Abs(estimatedScale - trueScale);

        return new ScaleBenchmarkResult(
            "S-D (Radial Projection)",
            sampleId,
            sourceType,
            estimatedScale,
            trueScale,
            error,
            margin,
            FwhmLogScale: 0.50d,
            sw.Elapsed.TotalMilliseconds,
            Success: error < 0.10d);
    }

    /// <summary>
    /// S-E: Sparse Scale Probes (Coarse Multi-scale Bitset matching with 3-point Parabolic Interpolation).
    /// </summary>
    public static ScaleBenchmarkResult EvaluateScaleMethodE(
        Mat queryEdges,
        Mat refEdges,
        double trueScale,
        string sampleId,
        string sourceType)
    {
        var sw = Stopwatch.StartNew();

        const int downsample = 4;
        var (refBitset, refW, refH) = BuildDownsampledBitset(refEdges, downsample);

        const int probeCount = 13;
        var minLogS = Math.Log(DomainMinScale);
        var maxLogS = Math.Log(DomainMaxScale);
        var stepLogS = (maxLogS - minLogS) / (probeCount - 1);

        var probeScores = new double[probeCount];
        var bestIdx = 0;
        var bestScore = -1.0d;
        var secondScore = -1.0d;

        for (var i = 0; i < probeCount; i++)
        {
            var logS = minLogS + i * stepLogS;
            var scale = Math.Exp(logS);

            var score = FastBitsetCorrelationAtScale(queryEdges, refBitset, refW, refH, scale, downsample);
            probeScores[i] = score;

            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestIdx = i;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        double refinedLogS;
        if (bestIdx > 0 && bestIdx < probeCount - 1)
        {
            var yPrev = probeScores[bestIdx - 1];
            var yCurr = probeScores[bestIdx];
            var yNext = probeScores[bestIdx + 1];
            var denom = (yPrev - 2.0 * yCurr + yNext);

            if (Math.Abs(denom) > Epsilon && yCurr >= yPrev && yCurr >= yNext)
            {
                var delta = 0.5d * (yPrev - yNext) / denom;
                delta = Math.Clamp(delta, -1.0d, 1.0d);
                refinedLogS = (minLogS + bestIdx * stepLogS) + delta * stepLogS;
            }
            else
            {
                refinedLogS = minLogS + bestIdx * stepLogS;
            }
        }
        else
        {
            refinedLogS = minLogS + bestIdx * stepLogS;
        }

        var estimatedScale = Math.Clamp(Math.Exp(refinedLogS), DomainMinScale, DomainMaxScale);
        sw.Stop();

        var margin = (bestScore - secondScore) / (bestScore + Epsilon);
        var halfMax = (bestScore + probeScores.Min()) * 0.5;
        var fwhm = 0.0;
        for (var i = 0; i < probeCount; i++)
        {
            if (probeScores[i] >= halfMax)
                fwhm += stepLogS;
        }

        var error = Math.Abs(estimatedScale - trueScale);
        var success = error < 0.035d;

        return new ScaleBenchmarkResult(
            "S-E (Sparse Scale Probes + Parabolic)",
            sampleId,
            sourceType,
            estimatedScale,
            trueScale,
            error,
            margin,
            fwhm,
            sw.Elapsed.TotalMilliseconds,
            success);
    }

    /// <summary>
    /// S-B: PeakRatio Gate ROC Evaluation across thresholds.
    /// </summary>
    public static List<PeakRatioRocPoint> EvaluateSBPeakRatioRoc(
        List<GroundTruthSample> samples,
        Dictionary<string, Mat> extractedEdges,
        double[] thresholds)
    {
        var rocPoints = new List<PeakRatioRocPoint>();

        var scoredSamples = new List<(double TrueScale, double EstScale, double Ratio)>();
        foreach (var s in samples)
        {
            var qEdges = extractedEdges[s.Id];
            var rEdges = s.ReferenceStructureLine;
            var (res, ratio) = EvaluateScaleMethodB(qEdges, rEdges, s.TrueScale, s.Id, s.SourceType);
            scoredSamples.Add((s.TrueScale, res.EstimatedScale, ratio));
        }

        foreach (var th in thresholds)
        {
            var passed = scoredSamples.Where(s => s.Ratio >= th).ToList();
            var coverage = (double)passed.Count / scoredSamples.Count * 100.0;

            if (passed.Count == 0)
            {
                rocPoints.Add(new PeakRatioRocPoint(th, 0.0, 0, 0, 0, 0));
                continue;
            }

            var errors = passed.Select(s => Math.Abs(s.EstScale - s.TrueScale)).OrderBy(x => x).ToList();
            var p50 = Percentile(errors, 0.50);
            var p95 = Percentile(errors, 0.95);
            var max = errors.Max();
            var catastrophicCount = passed.Count(s => Math.Abs(s.EstScale - s.TrueScale) > 0.05d);
            var catastrophicRate = (double)catastrophicCount / passed.Count * 100.0;

            rocPoints.Add(new PeakRatioRocPoint(th, coverage, p50, p95, max, catastrophicRate));
        }

        return rocPoints;
    }

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
