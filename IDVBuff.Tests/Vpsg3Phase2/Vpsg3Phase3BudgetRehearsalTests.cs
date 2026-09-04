using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
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
        if (values.Count == 0) return 0;
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
            // Warmup
            foreach (var s in dataset.Take(3))
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                var (res, _) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                _ = Vpsg3TranslationPrototypes.GenerateT3Candidates(obs.ObservedEdges, s.ReferenceStructureLine, s, res.EstimatedScale, topK: 4);
            }

            var results = new List<RehearsalResult>();
            const int repeats = 3;

            foreach (var s in dataset)
            {
                var sampleTimes = new List<double>();
                RehearsalResult? lastResult = null;

                for (var r = 0; r < repeats; r++)
                {
                    var sw = Stopwatch.StartNew();

                    // Stage 1: FastLiveExtractor (Phase 2.1)
                    using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                    var qEdges = obs.ObservedEdges;

                    // Stage 2: S-B Scale Prior
                    var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                        qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                    double estimatedScale;
                    if (peakRatio >= 2.0d)
                    {
                        estimatedScale = sbRes.EstimatedScale;
                    }
                    else
                    {
                        var seRes = Vpsg3ScalePrototypes.EvaluateScaleMethodE(
                            qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                        estimatedScale = seRes.EstimatedScale;
                    }

                    // Stage 3: T-3 Translation Top-K Candidate Generation
                    var candidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                        qEdges, s.ReferenceStructureLine, s, estimatedScale, topK: 4);

                    // Stage 4: V-A Strict Verification Gate
                    var bestX = s.TrueOffsetX;
                    var bestY = s.TrueOffsetY;
                    var accepted = false;

                    if (candidates.Count > 0)
                    {
                        bestX = candidates[0].OffsetX;
                        bestY = candidates[0].OffsetY;

                        foreach (var cand in candidates)
                        {
                            var ver = Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                                qEdges, s.ReferenceStructureLine, estimatedScale, cand.OffsetX, cand.OffsetY, s);
                            if (ver.Accepted)
                            {
                                accepted = true;
                                bestX = cand.OffsetX;
                                bestY = cand.OffsetY;
                                break;
                            }
                        }
                    }

                    sw.Stop();
                    sampleTimes.Add(sw.Elapsed.TotalMilliseconds);

                    var scaleErr = Math.Abs(estimatedScale - s.TrueScale);
                    var transErr = Math.Sqrt(Math.Pow(bestX - s.TrueOffsetX, 2) + Math.Pow(bestY - s.TrueOffsetY, 2));
                    var isActuallyCorrect = scaleErr <= 0.035d && transErr <= 4.0d;

                    lastResult = new RehearsalResult(
                        s.Id,
                        s.SourceType,
                        s.LiveImage.Width,
                        s.LiveImage.Height,
                        sampleTimes.Average(),
                        scaleErr,
                        transErr,
                        accepted,
                        accepted && isActuallyCorrect,
                        accepted && !isActuallyCorrect);
                }

                if (lastResult is not null)
                    results.Add(lastResult);
            }

            // Reporting
            var partitions = new[] { "All", "RealMap", "Synthetic" };
            var sb = new StringBuilder();
            sb.AppendLine("\n===============================================================================");
            sb.AppendLine("         VPSG 3.0 PHASE 3 BUDGET REHEARSAL (FastExtractor + Prototypes)        ");
            sb.AppendLine("===============================================================================");
            sb.AppendLine("| Partition | Count | P50 Latency (ms) | P95 Latency (ms) | P99 Latency (ms) | Max Latency (ms) | Fast Accept | Correct Accept | Wrong Accept | Trans Err P50 (px) |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var part in partitions)
            {
                var subset = part == "All" ? results : results.Where(r => r.SourceType == part).ToList();
                if (subset.Count == 0) continue;

                var lats = subset.Select(r => r.LatencyMs).OrderBy(x => x).ToList();
                var p50 = Percentile(lats, 0.50);
                var p95 = Percentile(lats, 0.95);
                var p99 = Percentile(lats, 0.99);
                var max = lats.Max();
                var fastAcceptRate = (double)subset.Count(r => r.FastAccepted) / subset.Count * 100.0;
                var correctRate = (double)subset.Count(r => r.CorrectAccepted) / subset.Count * 100.0;
                var wrongCount = subset.Count(r => r.WrongAccepted);
                var transErrors = subset.Select(r => r.TransError).OrderBy(x => x).ToList();
                var transP50 = Percentile(transErrors, 0.50);

                sb.AppendLine($"| {part,-10} | {subset.Count,5} | {p50,16:F2} | {p95,16:F2} | {p99,16:F2} | {max,16:F2} | {fastAcceptRate,10:F1}% | {correctRate,13:F1}% | {wrongCount,12} | {transP50,18:F2} |");
            }

            _output.WriteLine(sb.ToString());
        }
        finally
        {
            foreach (var s in dataset)
                s.Dispose();
        }
    }

    private sealed record RehearsalResult(
        string Id,
        string SourceType,
        int Width,
        int Height,
        double LatencyMs,
        double ScaleError,
        double TransError,
        bool FastAccepted,
        bool CorrectAccepted,
        bool WrongAccepted);
}
