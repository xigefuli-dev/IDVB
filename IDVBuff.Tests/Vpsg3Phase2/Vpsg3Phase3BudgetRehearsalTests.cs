using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3Phase3BudgetRehearsalTests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase3BudgetRehearsalTests(ITestOutputHelper output)
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

    [Fact]
    public void Benchmark_Phase3BudgetRehearsal()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var refDilatedK5 = new Dictionary<string, Mat>();
            var refDilatedK3 = new Dictionary<string, Mat>();
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

            foreach (var s in dataset)
            {
                if (!refDilatedK5.ContainsKey(s.ReferenceName))
                {
                    var d5 = new Mat();
                    Cv2.Dilate(s.ReferenceStructureLine, d5, k5);
                    refDilatedK5[s.ReferenceName] = d5;

                    var d3 = new Mat();
                    Cv2.Dilate(s.ReferenceStructureLine, d3, k3);
                    refDilatedK3[s.ReferenceName] = d3;
                }
            }

            // Warmup
            foreach (var s in dataset.Take(2))
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                var (res, _) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                _ = Vpsg3TranslationPrototypes.GenerateT3Candidates(obs.ObservedEdges, s.ReferenceStructureLine, s, res.EstimatedScale, topK: 4);
            }

            var results = new List<SampleRehearsalResult>();
            var sb = new StringBuilder();
            const int repeats = 3;

            foreach (var s in dataset)
            {
                var sampleTimes = new List<double>();
                SampleRehearsalResult? lastResult = null;

                for (var r = 0; r < repeats; r++)
                {
                    var sw = Stopwatch.StartNew();

                    // Stage 1: FastLiveExtractor
                    using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                    var qEdges = obs.ObservedEdges;

                    // Stage 2: S-B Scale Prior (Fast Reject on Gate Failure -> VPSG2)
                    var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                        qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);

                    if (peakRatio < 2.0d)
                    {
                        // Immediate fast-reject to VPSG2 (S-E banned in Fast Path)
                        sw.Stop();
                        sampleTimes.Add(sw.Elapsed.TotalMilliseconds);
                        lastResult = new SampleRehearsalResult(
                            s.Id, s.SourceType, sampleTimes.Average(),
                            CandidateGenerated: false, Top1GtRecall: false, Top4GtRecall: false,
                            NoCandidates: false, FastAccepted: false, CorrectAccepted: false, WrongAccepted: false,
                            ScaleError: double.NaN, TransError: double.NaN, IsFallback: true);
                        continue;
                    }

                    var estimatedScale = sbRes.EstimatedScale;

                    // Stage 3: T-3 Translation Top-K Candidate Generation
                    var candidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                        qEdges, s.ReferenceStructureLine, s, estimatedScale, topK: 4);

                    if (candidates.Count == 0)
                    {
                        sw.Stop();
                        sampleTimes.Add(sw.Elapsed.TotalMilliseconds);
                        lastResult = new SampleRehearsalResult(
                            s.Id, s.SourceType, sampleTimes.Average(),
                            CandidateGenerated: false, Top1GtRecall: false, Top4GtRecall: false,
                            NoCandidates: true, FastAccepted: false, CorrectAccepted: false, WrongAccepted: false,
                            ScaleError: double.NaN, TransError: double.NaN, IsFallback: true);
                        continue;
                    }

                    // Check GT Recall among raw candidates (within 8.0px GT tolerance)
                    var top1Gt = Math.Sqrt(Math.Pow(candidates[0].OffsetX - s.TrueOffsetX, 2) + Math.Pow(candidates[0].OffsetY - s.TrueOffsetY, 2)) <= 8.0d;
                    var top4Gt = candidates.Any(c => Math.Sqrt(Math.Pow(c.OffsetX - s.TrueOffsetX, 2) + Math.Pow(c.OffsetY - s.TrueOffsetY, 2)) <= 8.0d);

                    var refD5 = refDilatedK5[s.ReferenceName];
                    var refD3 = refDilatedK3[s.ReferenceName];

                    // Stage 4: Top-K Centered Refinement + Weighted Potential Field + Spatial Verification
                    var refinedCands = new List<(double Scale, double X, double Y, double Score, Vpsg3Phase3ACorrectnessSuite.SpatialVerificationResult Spatial)>();
                    foreach (var c in candidates)
                    {
                        var (rfS, rfX, rfY, rfScore) = Vpsg3Phase3ACorrectnessSuite.LocalRefineScaleAndTranslation(
                            obs.SparseEdgePoints, refD5, refD3, estimatedScale, c.OffsetX, c.OffsetY,
                            s.ViewportBounds, obs.Width, obs.Height);

                        var sp = Vpsg3Phase3ACorrectnessSuite.EvaluateSpatialVerification(
                            obs.SparseEdgePoints, refD5, rfS, rfX, rfY,
                            s.ViewportBounds, obs.Width, obs.Height, minPartitionsRequired: 2);

                        refinedCands.Add((rfS, rfX, rfY, rfScore, sp));
                    }

                    refinedCands.Sort((a, b) => b.Score.CompareTo(a.Score));

                    var best = refinedCands[0];
                    var secondScore = 0.0d;
                    for (var i = 1; i < refinedCands.Count; i++)
                    {
                        var dist = Math.Sqrt(Math.Pow(refinedCands[i].X - best.X, 2) + Math.Pow(refinedCands[i].Y - best.Y, 2));
                        if (dist > 6.0d)
                        {
                            secondScore = refinedCands[i].Score;
                            break;
                        }
                    }

                    var margin = best.Score - secondScore;
                    sw.Stop();
                    sampleTimes.Add(sw.Elapsed.TotalMilliseconds);

                    // Check aperture margin using standard K5 spatial global score
                    var secondK5Score = 0.0d;
                    for (var i = 1; i < refinedCands.Count; i++)
                    {
                        var dist = Math.Sqrt(Math.Pow(refinedCands[i].X - best.X, 2) + Math.Pow(refinedCands[i].Y - best.Y, 2));
                        if (dist > 6.0d)
                        {
                            secondK5Score = refinedCands[i].Spatial.GlobalScore;
                            break;
                        }
                    }

                    var k5Margin = best.Spatial.GlobalScore - secondK5Score;
                    sw.Stop();
                    sampleTimes.Add(sw.Elapsed.TotalMilliseconds);

                    // Phase 3A Gate Rule: MinScore >= 0.60, k5Margin >= 0.09, PassedPartitions >= 3
                    var passGate = best.Score >= 0.60d && k5Margin >= 0.09d && best.Spatial.PassedPartitions >= 3;

                    if (passGate)
                    {
                        var scaleErr = Math.Abs(best.Scale - s.TrueScale);
                        var transErr = Math.Sqrt(Math.Pow(best.X - s.TrueOffsetX, 2) + Math.Pow(best.Y - s.TrueOffsetY, 2));
                        var isActuallyCorrect = scaleErr <= 0.035d && transErr <= 4.0d;

                        lastResult = new SampleRehearsalResult(
                            s.Id, s.SourceType, sampleTimes.Average(),
                            CandidateGenerated: true, Top1GtRecall: top1Gt, Top4GtRecall: top4Gt,
                            NoCandidates: false, FastAccepted: true, CorrectAccepted: isActuallyCorrect, WrongAccepted: !isActuallyCorrect,
                            ScaleError: scaleErr, TransError: transErr, IsFallback: false);
                    }
                    else
                    {
                        // Rejected by gate -> fallback to VPSG2. No GT imputation!
                        lastResult = new SampleRehearsalResult(
                            s.Id, s.SourceType, sampleTimes.Average(),
                            CandidateGenerated: true, Top1GtRecall: top1Gt, Top4GtRecall: top4Gt,
                            NoCandidates: false, FastAccepted: false, CorrectAccepted: false, WrongAccepted: false,
                            ScaleError: double.NaN, TransError: double.NaN, IsFallback: true);
                    }
                }

                if (lastResult is not null)
                    results.Add(lastResult);
            }

            // Reporting Tables
            var partitions = new[] { "All", "RealMap", "Synthetic" };
            sb.AppendLine("\n=================================================================================================================================");
            sb.AppendLine("                     VPSG 3.0 PHASE 3A CORRECTNESS CONVERGENCE END-TO-END JOINT BENCHMARK                        ");
            sb.AppendLine("=================================================================================================================================");
            sb.AppendLine("| Partition | Count | CandGen | Top1 GT | Top4 GT | NoCand | FastAccCov | Precision | WrongAcc | Fallback | Latency P50 | Fast P50 | FBR P50 | Trans P50 |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var part in partitions)
            {
                var subset = part == "All" ? results : results.Where(r => r.SourceType == part).ToList();
                if (subset.Count == 0) continue;

                var total = subset.Count;
                var candGen = (double)subset.Count(r => r.CandidateGenerated) / total * 100.0;
                var top1Recall = (double)subset.Count(r => r.Top1GtRecall) / total * 100.0;
                var top4Recall = (double)subset.Count(r => r.Top4GtRecall) / total * 100.0;
                var noCandRate = (double)subset.Count(r => r.NoCandidates) / total * 100.0;
                var fastAccCount = subset.Count(r => r.FastAccepted);
                var fastAccCov = (double)fastAccCount / total * 100.0;
                var correctCount = subset.Count(r => r.CorrectAccepted);
                var wrongCount = subset.Count(r => r.WrongAccepted);
                var prec = fastAccCount > 0 ? (double)correctCount / fastAccCount * 100.0 : 100.0;
                var fallbackRate = (double)subset.Count(r => r.IsFallback) / total * 100.0;

                var allLats = subset.Select(r => r.LatencyMs).OrderBy(x => x).ToList();
                var fastLats = subset.Where(r => r.FastAccepted).Select(r => r.LatencyMs).OrderBy(x => x).ToList();
                var fbrLats = subset.Where(r => r.IsFallback).Select(r => r.LatencyMs).OrderBy(x => x).ToList();

                var p50All = Percentile(allLats, 0.50);
                var p50Fast = fastLats.Count > 0 ? Percentile(fastLats, 0.50) : double.NaN;
                var p50Fbr = fbrLats.Count > 0 ? Percentile(fbrLats, 0.50) : double.NaN;

                var validTrans = subset.Where(r => !double.IsNaN(r.TransError)).Select(r => r.TransError).OrderBy(x => x).ToList();
                var transP50 = validTrans.Count > 0 ? Percentile(validTrans, 0.50) : double.NaN;

                sb.AppendLine($"| {part,-9} | {total,5} | {candGen,6:F1}% | {top1Recall,6:F1}% | {top4Recall,6:F1}% | {noCandRate,5:F1}% | {fastAccCov,9:F1}% | {prec,8:F1}% | {wrongCount,8} | {fallbackRate,7:F1}% | {p50All,10:F2}ms | {p50Fast,7:F2}ms | {p50Fbr,6:F2}ms | {transP50,8:F2}px |");
            }

            foreach (var w in results.Where(r => r.WrongAccepted))
            {
                sb.AppendLine($"[WRONG_ACCEPT_FOUND] {w.Id}: ScaleErr={w.ScaleError:F4}, TransErr={w.TransError:F2}px");
            }

            var reportStr = sb.ToString();
            _output.WriteLine(reportStr);

            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                File.WriteAllText(Path.Combine(scratchDir, "phase3a_benchmark_rehearsal.txt"), reportStr);
            }
            catch { }

            foreach (var kvp in refDilatedK5) kvp.Value.Dispose();
            foreach (var kvp in refDilatedK3) kvp.Value.Dispose();
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    private sealed record SampleRehearsalResult(
        string Id,
        string SourceType,
        double LatencyMs,
        bool CandidateGenerated,
        bool Top1GtRecall,
        bool Top4GtRecall,
        bool NoCandidates,
        bool FastAccepted,
        bool CorrectAccepted,
        bool WrongAccepted,
        double ScaleError,
        double TransError,
        bool IsFallback);
}
