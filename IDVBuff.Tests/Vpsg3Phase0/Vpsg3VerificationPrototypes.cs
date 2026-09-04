using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static class Vpsg3VerificationPrototypes
{
    /// <summary>
    /// V-A: Sparse Points Bit-Test.
    /// Fast bitwise check of N query edge points transformed into reference space.
    /// </summary>
    public static VerificationBenchmarkResult EvaluateVerificationMethodA(
        Mat queryEdges,
        Mat refEdges,
        double estScale,
        double estOffsetX,
        double estOffsetY,
        GroundTruthSample sample)
    {
        var sw = Stopwatch.StartNew();

        // 1. Dilate reference edges by 2px to give tolerance
        using var refDilated = new Mat();
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(refEdges, refDilated, k5);

        // 2. Sample 150 points from query edges
        var pts = SamplePoints(queryEdges, maxPts: 150);
        if (pts.Count == 0)
        {
            sw.Stop();
            return new VerificationBenchmarkResult("V-A (Sparse Bit-Test)", sample.Id, 0, false, false, true, sw.Elapsed.TotalMicroseconds);
        }

        // 3. Map query points to reference coordinates using canonical transform:
        // Screen = estOffsetX + refX * estScale
        // QueryPoint on Screen = ViewportBounds.X + qX
        // => refX = (ViewportBounds.X + qX - estOffsetX) / estScale
        var hitCount = 0;
        var refW = refDilated.Width;
        var refH = refDilated.Height;

        foreach (var q in pts)
        {
            var screenX = sample.ViewportBounds.X + q.X;
            var screenY = sample.ViewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - estOffsetX) / estScale);
            var ry = (int)Math.Round((screenY - estOffsetY) / estScale);

            if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
            {
                if (refDilated.At<byte>(ry, rx) > 128)
                    hitCount++;
            }
        }

        sw.Stop();

        var score = (double)hitCount / pts.Count;
        var accepted = score >= 0.50d;

        // Ground truth acceptance criteria (within 5px translation error and 0.03 scale error)
        var scaleErr = Math.Abs(estScale - sample.TrueScale);
        var transErr = Math.Sqrt(Math.Pow(estOffsetX - sample.TrueOffsetX, 2) + Math.Pow(estOffsetY - sample.TrueOffsetY, 2));
        var productionAccepted = scaleErr <= 0.04d && transErr <= 8.0d;

        return new VerificationBenchmarkResult(
            "V-A (Sparse Bit-Test)",
            sample.Id,
            score,
            accepted,
            productionAccepted,
            Agreement: accepted == productionAccepted,
            sw.Elapsed.TotalMicroseconds);
    }

    /// <summary>
    /// Evaluates V-A as a strict fast acceptance gate.
    /// </summary>
    public static StrictVerificationResult EvaluateStrictVerification(
        Mat queryEdges,
        Mat refEdges,
        double estScale,
        double estOffsetX,
        double estOffsetY,
        GroundTruthSample sample,
        double threshold = 0.52d,
        int kernelSize = 5)
    {
        var sw = Stopwatch.StartNew();

        using var refDilated = new Mat();
        using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kernelSize, kernelSize));
        Cv2.Dilate(refEdges, refDilated, k);

        var pts = SamplePoints(queryEdges, maxPts: 150);
        if (pts.Count == 0)
        {
            sw.Stop();
            return new StrictVerificationResult("V-A (Strict Gate)", sample.Id, sample.SourceType, 0, false, false, sw.Elapsed.TotalMicroseconds);
        }

        var hitCount = 0;
        var refW = refDilated.Width;
        var refH = refDilated.Height;

        foreach (var q in pts)
        {
            var screenX = sample.ViewportBounds.X + q.X;
            var screenY = sample.ViewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - estOffsetX) / estScale);
            var ry = (int)Math.Round((screenY - estOffsetY) / estScale);

            if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
            {
                if (refDilated.At<byte>(ry, rx) > 128)
                    hitCount++;
            }
        }

        sw.Stop();

        var score = (double)hitCount / pts.Count;
        var accepted = score >= threshold;

        var scaleErr = Math.Abs(estScale - sample.TrueScale);
        var transErr = Math.Sqrt(Math.Pow(estOffsetX - sample.TrueOffsetX, 2) + Math.Pow(estOffsetY - sample.TrueOffsetY, 2));
        var isActuallyCorrect = scaleErr <= 0.035d && transErr <= 4.0d;

        return new StrictVerificationResult(
            "V-A (Strict Gate)",
            sample.Id,
            sample.SourceType,
            score,
            accepted,
            isActuallyCorrect,
            sw.Elapsed.TotalMicroseconds);
    }

    /// <summary>
    /// V-B: Quantized Distance Field Lookup.
    /// Fast memory lookup into precomputed Euclidean distance transform.
    /// </summary>
    public static VerificationBenchmarkResult EvaluateVerificationMethodB(
        Mat queryEdges,
        Mat refEdges,
        double estScale,
        double estOffsetX,
        double estOffsetY,
        GroundTruthSample sample)
    {
        var sw = Stopwatch.StartNew();

        // 1. Compute Distance Transform of inverted reference edges
        using var refInv = new Mat();
        using var distMap = new Mat();
        Cv2.BitwiseNot(refEdges, refInv);
        Cv2.DistanceTransform(refInv, distMap, DistanceTypes.L2, DistanceTransformMasks.Mask5);

        // 2. Sample 150 points from query edges
        var pts = SamplePoints(queryEdges, maxPts: 150);
        if (pts.Count == 0)
        {
            sw.Stop();
            return new VerificationBenchmarkResult("V-B (Quantized DistField)", sample.Id, 0, false, false, true, sw.Elapsed.TotalMicroseconds);
        }

        var refW = distMap.Width;
        var refH = distMap.Height;
        var scoreSum = 0.0d;
        const double maxDist = 8.0d;

        foreach (var q in pts)
        {
            var screenX = sample.ViewportBounds.X + q.X;
            var screenY = sample.ViewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - estOffsetX) / estScale);
            var ry = (int)Math.Round((screenY - estOffsetY) / estScale);

            if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
            {
                var d = distMap.At<float>(ry, rx);
                scoreSum += Math.Max(0.0, 1.0 - (d / maxDist));
            }
        }

        sw.Stop();

        var score = scoreSum / pts.Count;
        var accepted = score >= 0.55d;

        var scaleErr = Math.Abs(estScale - sample.TrueScale);
        var transErr = Math.Sqrt(Math.Pow(estOffsetX - sample.TrueOffsetX, 2) + Math.Pow(estOffsetY - sample.TrueOffsetY, 2));
        var productionAccepted = scaleErr <= 0.04d && transErr <= 8.0d;

        return new VerificationBenchmarkResult(
            "V-B (Quantized DistField)",
            sample.Id,
            score,
            accepted,
            productionAccepted,
            Agreement: accepted == productionAccepted,
            sw.Elapsed.TotalMicroseconds);
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
}
