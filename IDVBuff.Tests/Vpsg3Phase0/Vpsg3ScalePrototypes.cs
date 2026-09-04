using System.Diagnostics;
using System.Numerics;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static partial class Vpsg3ScalePrototypes
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

}
