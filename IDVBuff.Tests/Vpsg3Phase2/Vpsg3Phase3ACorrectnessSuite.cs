using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3Phase3ACorrectnessSuite
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase3ACorrectnessSuite(ITestOutputHelper output)
    {
        _output = output;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(x => x).ToList();
        var idx = (int)Math.Floor(p * (sorted.Count - 1));
        return sorted[idx];
    }

    public sealed record SpatialVerificationResult(
        double GlobalScore,
        int TotalPoints,
        int HitPoints,
        int ValidPartitions,
        int PassedPartitions,
        bool IsSpatiallyConsistent);

    public static SpatialVerificationResult EvaluateSpatialVerification(
        IReadOnlyList<Point> sparsePoints,
        Mat refDilated,
        double estScale,
        double estOffsetX,
        double estOffsetY,
        MapScreenRect viewportBounds,
        int width,
        int height,
        int minPartitionsRequired = 2,
        double partitionThreshold = 0.50d)
    {
        if (sparsePoints.Count == 0)
            return new SpatialVerificationResult(0, 0, 0, 0, 0, false);

        var halfW = width / 2;
        var halfH = height / 2;
        var refW = refDilated.Width;
        var refH = refDilated.Height;

        // 2x2 Spatial partition counts: [TL, TR, BL, BR]
        var partTotal = new int[4];
        var partHits = new int[4];
        var totalHits = 0;

        foreach (var q in sparsePoints)
        {
            var partIdx = (q.X < halfW ? 0 : 1) + (q.Y < halfH ? 0 : 2);
            partTotal[partIdx]++;

            var screenX = viewportBounds.X + q.X;
            var screenY = viewportBounds.Y + q.Y;
            var rx = (int)Math.Round((screenX - estOffsetX) / estScale);
            var ry = (int)Math.Round((screenY - estOffsetY) / estScale);

            if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
            {
                if (refDilated.At<byte>(ry, rx) > 128)
                {
                    totalHits++;
                    partHits[partIdx]++;
                }
            }
        }

        var globalScore = (double)totalHits / sparsePoints.Count;
        var validParts = 0;
        var passedParts = 0;

        for (var p = 0; p < 4; p++)
        {
            // A partition is statistically valid if it has at least 5 points
            if (partTotal[p] >= 5)
            {
                validParts++;
                var ratio = (double)partHits[p] / partTotal[p];
                if (ratio >= partitionThreshold)
                {
                    passedParts++;
                }
            }
        }

        var consistent = validParts >= minPartitionsRequired && passedParts >= minPartitionsRequired;
        return new SpatialVerificationResult(globalScore, sparsePoints.Count, totalHits, validParts, passedParts, consistent);
    }

    public static (double RefinedScale, double RefinedX, double RefinedY, double BestScore) LocalRefineScaleAndTranslation(
        IReadOnlyList<Point> sparsePoints,
        Mat refDilated,
        double seedScale,
        double seedX,
        double seedY,
        MapScreenRect viewportBounds,
        int width,
        int height) => LocalRefineScaleAndTranslation(sparsePoints, refDilated, refDilated, seedScale, seedX, seedY, viewportBounds, width, height);

    public static (double RefinedScale, double RefinedX, double RefinedY, double BestScore) LocalRefineScaleAndTranslation(
        IReadOnlyList<Point> sparsePoints,
        Mat refDilatedK5,
        Mat refDilatedK3,
        double seedScale,
        double seedX,
        double seedY,
        MapScreenRect viewportBounds,
        int width,
        int height)
    {
        var cx = viewportBounds.X + width / 2.0d;
        var cy = viewportBounds.Y + height / 2.0d;
        var rcx = (cx - seedX) / seedScale;
        var rcy = (cy - seedY) / seedScale;

        var scaleDeltas = new[] { 0.000d, -0.005d, 0.005d, -0.010d, 0.010d, -0.015d, 0.015d, -0.020d, 0.020d };
        var transDeltas = new[] { 0.0d, -2.0d, 2.0d, -4.0d, 4.0d, -6.0d, 6.0d };

        var bestScore = -1.0d;
        var bestScale = seedScale;
        var bestX = seedX;
        var bestY = seedY;

        var refW = refDilatedK5.Width;
        var refH = refDilatedK5.Height;

        foreach (var ds in scaleDeltas)
        {
            var curScale = seedScale + ds;
            if (curScale < 0.65d || curScale > 1.60d) continue;

            var baseX = cx - rcx * curScale;
            var baseY = cy - rcy * curScale;

            foreach (var dx in transDeltas)
            {
                var curX = baseX + dx;
                foreach (var dy in transDeltas)
                {
                    var curY = baseY + dy;
                    var hitsK5 = 0;
                    var hitsK3 = 0;

                    foreach (var q in sparsePoints)
                    {
                        var screenX = viewportBounds.X + q.X;
                        var screenY = viewportBounds.Y + q.Y;
                        var rx = (int)Math.Round((screenX - curX) / curScale);
                        var ry = (int)Math.Round((screenY - curY) / curScale);

                        if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
                        {
                            if (refDilatedK5.At<byte>(ry, rx) > 128)
                            {
                                hitsK5++;
                                if (refDilatedK3.At<byte>(ry, rx) > 128)
                                    hitsK3++;
                            }
                        }
                    }

                    // Weighted convex potential field: centers alignment and eliminates 5x5 plateau
                    var score = (hitsK5 + 2.0d * hitsK3) / (3.0d * sparsePoints.Count);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestScale = curScale;
                        bestX = curX;
                        bestY = curY;
                    }
                }
            }
        }

        return (bestScale, bestX, bestY, bestScore);
    }

    [Fact]
    public void RunAllPhase3AConvergenceExperiments()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            // Precompile dilated references for fast verification
            var refDilatedMapK5 = new Dictionary<string, Mat>();
            var refDilatedMapK3 = new Dictionary<string, Mat>();
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

            foreach (var s in dataset)
            {
                if (!refDilatedMapK5.ContainsKey(s.ReferenceName))
                {
                    var dil5 = new Mat();
                    Cv2.Dilate(s.ReferenceStructureLine, dil5, k5);
                    refDilatedMapK5[s.ReferenceName] = dil5;

                    var dil3 = new Mat();
                    Cv2.Dilate(s.ReferenceStructureLine, dil3, k3);
                    refDilatedMapK3[s.ReferenceName] = dil3;
                }
            }

            // -----------------------------------------------------------------
            // EXPERIMENT 1 & 2: Grid Search over Margin, PeakRatio & Spatial Consistency
            // -----------------------------------------------------------------
            _output.WriteLine("===============================================================================");
            _output.WriteLine("       EXPERIMENT 1 & 2: JOINT GATE ROC (All-Candidate Margin + Spatial)       ");
            _output.WriteLine("===============================================================================");

            var peakGates = new[] { 2.0d, 2.5d, 3.0d };
            var scoreThresholds = new[] { 0.55d, 0.65d, 0.70d };
            var scoreMargins = new[] { 0.00d, 0.03d, 0.05d, 0.08d };
            var spatialModes = new[] { false, true };

            var sbRoc = new StringBuilder();
            sbRoc.AppendLine("| PeakGate | MinScore | Margin | Spatial2x2 | FastAcceptCov | Precision | WrongAccept | FallbackRate |");
            sbRoc.AppendLine("| :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var pg in peakGates)
            {
                foreach (var st in scoreThresholds)
                {
                    foreach (var sm in scoreMargins)
                    {
                        foreach (var sp in spatialModes)
                        {
                            var total = dataset.Count;
                            var fastAccepted = 0;
                            var correctAccepted = 0;
                            var wrongAccepted = 0;
                            var fallbacks = 0;

                            foreach (var s in dataset)
                            {
                                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                                var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                                    obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);

                                if (peakRatio < pg)
                                {
                                    // S-B Gate failed -> Reject to VPSG2 immediately
                                    fallbacks++;
                                    continue;
                                }

                                var estScale = sbRes.EstimatedScale;
                                var candidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                                    obs.ObservedEdges, s.ReferenceStructureLine, s, estScale, topK: 4);

                                if (candidates.Count == 0)
                                {
                                    fallbacks++;
                                    continue;
                                }

                                var refDilK5 = refDilatedMapK5[s.ReferenceName];
                                var refDilK3 = refDilatedMapK3[s.ReferenceName];

                                // Evaluate ALL candidates
                                var scoredCands = new List<(double Score, SpatialVerificationResult Ver, double X, double Y)>();
                                foreach (var c in candidates)
                                {
                                    var v = EvaluateSpatialVerification(
                                        obs.SparseEdgePoints, refDilK5, estScale, c.OffsetX, c.OffsetY,
                                        s.ViewportBounds, obs.Width, obs.Height, minPartitionsRequired: 2);
                                    scoredCands.Add((v.GlobalScore, v, c.OffsetX, c.OffsetY));
                                }

                                scoredCands.Sort((a, b) => b.Score.CompareTo(a.Score));

                                var best = scoredCands[0];
                                var secondScore = scoredCands.Count > 1 ? scoredCands[1].Score : 0d;
                                var margin = best.Score - secondScore;

                                // If refinement enabled: refine top candidates with weighted potential field
                                var (refScale, refX, refY, refScore) = LocalRefineScaleAndTranslation(
                                    obs.SparseEdgePoints, refDilK5, refDilK3, estScale, best.X, best.Y,
                                    s.ViewportBounds, obs.Width, obs.Height);

                                var finalScale = refScale;
                                var finalX = refX;
                                var finalY = refY;
                                var finalVer = EvaluateSpatialVerification(
                                    obs.SparseEdgePoints, refDilK5, finalScale, finalX, finalY,
                                    s.ViewportBounds, obs.Width, obs.Height, minPartitionsRequired: 2);

                                var passScore = finalVer.GlobalScore >= st;
                                var passMargin = margin >= sm;
                                var passSpatial = !sp || finalVer.IsSpatiallyConsistent;

                                if (passScore && passMargin && passSpatial)
                                {
                                    fastAccepted++;
                                    var scaleErr = Math.Abs(finalScale - s.TrueScale);
                                    var transErr = Math.Sqrt(Math.Pow(finalX - s.TrueOffsetX, 2) + Math.Pow(finalY - s.TrueOffsetY, 2));
                                    var isActuallyCorrect = scaleErr <= 0.035d && transErr <= 4.0d;

                                    if (isActuallyCorrect)
                                        correctAccepted++;
                                    else
                                        wrongAccepted++;
                                }
                                else
                                {
                                    fallbacks++;
                                }
                            }

                            var cov = (double)fastAccepted / total * 100.0;
                            var prec = fastAccepted > 0 ? (double)correctAccepted / fastAccepted * 100.0 : 100.0;
                            var fbr = (double)fallbacks / total * 100.0;

                            if (wrongAccepted <= 3 && cov >= 40.0)
                            {
                                sbRoc.AppendLine($"| {pg,8:F1} | {st,8:F2} | {sm,6:F2} | {sp,10} | {cov,12:F1}% | {prec,8:F1}% | {wrongAccepted,11} | {fbr,11:F1}% |");
                            }
                        }
                    }
                }
            }

            _output.WriteLine(sbRoc.ToString());

            // -----------------------------------------------------------------
            // EXPERIMENT 3: Local Scale/Translation Refinement Benchmark
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine("           EXPERIMENT 3: LOCAL SCALE/TRANSLATION REFINEMENT PROBE              ");
            _output.WriteLine("===============================================================================");

            var scaleErrorsBefore = new List<double>();
            var scaleErrorsAfter = new List<double>();
            var transErrorsBefore = new List<double>();
            var transErrorsAfter = new List<double>();
            var refineTimes = new List<double>();

            foreach (var s in dataset)
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                    obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);

                if (peakRatio < 2.0d) continue;

                var estScale = sbRes.EstimatedScale;
                var candidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                    obs.ObservedEdges, s.ReferenceStructureLine, s, estScale, topK: 4);

                if (candidates.Count == 0) continue;

                var refDilK5 = refDilatedMapK5[s.ReferenceName];
                var refDilK3 = refDilatedMapK3[s.ReferenceName];
                var top1 = candidates[0];

                var scaleErrPre = Math.Abs(estScale - s.TrueScale);
                var transErrPre = Math.Sqrt(Math.Pow(top1.OffsetX - s.TrueOffsetX, 2) + Math.Pow(top1.OffsetY - s.TrueOffsetY, 2));

                scaleErrorsBefore.Add(scaleErrPre);
                transErrorsBefore.Add(transErrPre);

                var swRefine = Stopwatch.StartNew();
                var (refScale, refX, refY, _) = LocalRefineScaleAndTranslation(
                    obs.SparseEdgePoints, refDilK5, refDilK3, estScale, top1.OffsetX, top1.OffsetY,
                    s.ViewportBounds, obs.Width, obs.Height);
                swRefine.Stop();
                refineTimes.Add(swRefine.Elapsed.TotalMilliseconds);

                var scaleErrPost = Math.Abs(refScale - s.TrueScale);
                var transErrPost = Math.Sqrt(Math.Pow(refX - s.TrueOffsetX, 2) + Math.Pow(refY - s.TrueOffsetY, 2));

                scaleErrorsAfter.Add(scaleErrPost);
                transErrorsAfter.Add(transErrPost);
            }

            var sbRef = new StringBuilder();
            sbRef.AppendLine("| Metric | Before Refinement | After Refinement | Improvement |");
            sbRef.AppendLine("| :--- | :---: | :---: | :---: |");
            sbRef.AppendLine($"| Scale Error P50 | {Percentile(scaleErrorsBefore, 0.50),17:F4} | {Percentile(scaleErrorsAfter, 0.50),16:F4} | {(Percentile(scaleErrorsBefore, 0.50) - Percentile(scaleErrorsAfter, 0.50)) / Percentile(scaleErrorsBefore, 0.50) * 100.0,10:F1}% |");
            sbRef.AppendLine($"| Scale Error P95 | {Percentile(scaleErrorsBefore, 0.95),17:F4} | {Percentile(scaleErrorsAfter, 0.95),16:F4} | {(Percentile(scaleErrorsBefore, 0.95) - Percentile(scaleErrorsAfter, 0.95)) / Percentile(scaleErrorsBefore, 0.95) * 100.0,10:F1}% |");
            sbRef.AppendLine($"| Trans Error P50 | {Percentile(transErrorsBefore, 0.50),15:F2}px | {Percentile(transErrorsAfter, 0.50),14:F2}px | {(Percentile(transErrorsBefore, 0.50) - Percentile(transErrorsAfter, 0.50)) / Percentile(transErrorsBefore, 0.50) * 100.0,10:F1}% |");
            sbRef.AppendLine($"| Trans Error P95 | {Percentile(transErrorsBefore, 0.95),15:F2}px | {Percentile(transErrorsAfter, 0.95),14:F2}px | {(Percentile(transErrorsBefore, 0.95) - Percentile(transErrorsAfter, 0.95)) / Percentile(transErrorsBefore, 0.95) * 100.0,10:F1}% |");
            sbRef.AppendLine($"| Refine Latency  |             N/A |     P50: {Percentile(refineTimes, 0.50):F2}ms, P95: {Percentile(refineTimes, 0.95):F2}ms | 81-probe centered scan |");
            _output.WriteLine(sbRef.ToString());

            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                var fullReport = sbRoc.ToString() + "\n\n" + sbRef.ToString();
                File.WriteAllText(Path.Combine(scratchDir, "phase3a_convergence.txt"), fullReport);
            }
            catch { }

            // Cleanup
            foreach (var kvp in refDilatedMapK5)
                kvp.Value.Dispose();
            foreach (var kvp in refDilatedMapK3)
                kvp.Value.Dispose();
        }
        finally
        {
            foreach (var s in dataset)
                s.Dispose();
        }
    }
}
