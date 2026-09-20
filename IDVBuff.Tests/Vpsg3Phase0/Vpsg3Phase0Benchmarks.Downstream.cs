using System.Diagnostics;
using System.Text;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase0;

public sealed partial class Vpsg3Phase0Benchmarks
{
    #region Benchmark 4: T-3 Top-K Candidate Generator
    private void RunTranslationTopKBenchmark(
        List<GroundTruthSample> dataset,
        Dictionary<string, Mat> extractedEdges)
    {
        var topKResults = new List<TranslationTopKResult>();
        foreach (var s in dataset)
        {
            var qEdges = extractedEdges[s.Id];
            var rEdges = s.ReferenceStructureLine;
            topKResults.Add(Vpsg3TranslationPrototypes.EvaluateTranslationTopK(qEdges, rEdges, s, topK: 8));
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Partition | Top-1 Err P50 (px) | Top-1 Err P95 (px) | Top-1 <=2px | Top-1 <=3px | Top-1 <=5px | Top-2 Recall | Top-4 Recall | Top-8 Recall | Best in Top-8 Err | P50 (ms) |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        var partitions = new[] { "Real-Only", "Synthetic-Only", "Combined" };
        foreach (var part in partitions)
        {
            var items = part switch
            {
                "Real-Only" => topKResults.Where(r => r.SourceType == "RealMap").ToList(),
                "Synthetic-Only" => topKResults.Where(r => r.SourceType == "Synthetic").ToList(),
                _ => topKResults
            };
            if (items.Count == 0) continue;

            var errs = items.Select(r => r.Top1ErrorPixels).OrderBy(x => x).ToList();
            var bestErrs = items.Select(r => r.BestInTopKError).OrderBy(x => x).ToList();
            var lats = items.Select(r => r.ElapsedMs).OrderBy(x => x).ToList();

            var p50Err = Percentile(errs, 0.50);
            var p95Err = Percentile(errs, 0.95);
            var hit2 = (double)items.Count(r => r.Top1Hit2px) / items.Count * 100.0;
            var hit3 = (double)items.Count(r => r.Top1Hit3px) / items.Count * 100.0;
            var hit5 = (double)items.Count(r => r.Top1Hit5px) / items.Count * 100.0;

            var r2 = (double)items.Count(r => r.Top2Recall) / items.Count * 100.0;
            var r4 = (double)items.Count(r => r.Top4Recall) / items.Count * 100.0;
            var r8 = (double)items.Count(r => r.Top8Recall) / items.Count * 100.0;
            var bestP50 = Percentile(bestErrs, 0.50);
            var latP50 = Percentile(lats, 0.50);

            sb.AppendLine($"| {part,-14} | {p50Err,18:F2} | {p95Err,18:F2} | {hit2,10:F1}% | {hit3,10:F1}% | {hit5,10:F1}% | {r2,11:F1}% | {r4,11:F1}% | {r8,11:F1}% | {bestP50,17:F2} | {latP50,8:F2} |");
        }

        _output.WriteLine(sb.ToString());
    }

    #endregion

    #region Benchmark 5: V-A Strict Acceptance Gate

    private void RunStrictVerificationBenchmark(
        List<GroundTruthSample> dataset,
        Dictionary<string, Mat> extractedEdges)
    {
        var verResults = new List<StrictVerificationResult>();
        foreach (var s in dataset)
        {
            var qEdges = extractedEdges[s.Id];
            var rEdges = s.ReferenceStructureLine;

            // 1. Nominal match candidate
            verResults.Add(Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                qEdges, rEdges, s.TrueScale, s.TrueOffsetX, s.TrueOffsetY, s, threshold: 0.50));

            // 2. Deliberate negative false match candidates (+25px offset, -35px offset)
            verResults.Add(Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                qEdges, rEdges, s.TrueScale, s.TrueOffsetX + 25, s.TrueOffsetY + 25, s, threshold: 0.50));
            verResults.Add(Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                qEdges, rEdges, s.TrueScale, s.TrueOffsetX - 35, s.TrueOffsetY - 35, s, threshold: 0.50));
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Partition | Total Evaluations | Accepted Count | Correct Accepted | Wrong Accepted (FPR) | Accepted Precision | Correct Rejected | Fast-Path Coverage | P50 Latency (μs) |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        var partitions = new[] { "Real-Only", "Synthetic-Only", "Combined" };
        foreach (var part in partitions)
        {
            var items = part switch
            {
                "Real-Only" => verResults.Where(r => r.SourceType == "RealMap").ToList(),
                "Synthetic-Only" => verResults.Where(r => r.SourceType == "Synthetic").ToList(),
                _ => verResults
            };
            if (items.Count == 0) continue;

            var total = items.Count;
            var accepted = items.Where(r => r.Accepted).ToList();
            var accCount = accepted.Count;
            var correctAcc = accepted.Count(r => r.IsActuallyCorrect);
            var wrongAcc = accepted.Count(r => !r.IsActuallyCorrect);
            var accPrec = accCount > 0 ? (double)correctAcc / accCount * 100.0 : 100.0;

            var rejected = items.Where(r => !r.Accepted).ToList();
            var correctRej = rejected.Count(r => !r.IsActuallyCorrect);

            var truePositives = items.Count(r => r.IsActuallyCorrect);
            var fastPathCov = truePositives > 0 ? (double)correctAcc / truePositives * 100.0 : 0.0;

            var lats = items.Select(r => r.ElapsedMicroseconds).OrderBy(x => x).ToList();
            var latP50 = Percentile(lats, 0.50);

            sb.AppendLine($"| {part,-14} | {total,17} | {accCount,14} | {correctAcc,16} | {wrongAcc,20} | {accPrec,17:F1}% | {correctRej,16} | {fastPathCov,17:F1}% | {latP50,16:F1} |");
        }

        _output.WriteLine(sb.ToString());
    }

    #endregion

    #region Benchmark 6: Genuine End-to-End Joint Benchmark

    private void RunGenuineEndToEndBenchmark(
        List<GroundTruthSample> dataset,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid synPyramid,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid? realPyramid)
    {
        // 1. Warm-up runs (3 iterations) to ensure JIT compile and cache warming
        for (var w = 0; w < 3; w++)
        {
            var warmupSample = dataset[0];
            using var warmupEdges = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(warmupSample.LiveImage).Edges;
            _ = Vpsg3ScalePyramidPrototype.Search(warmupEdges, synPyramid, warmupSample, topK: 4);
        }

        // 2. Measure Cold-Run separately on first sample
        var coldSample = dataset[0];
        var coldSw = Stopwatch.StartNew();
        using (var coldEdges = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(coldSample.LiveImage).Edges)
        {
            var coldPyr = coldSample.SourceType == "RealMap" && realPyramid is not null ? realPyramid : synPyramid;
            var coldRes = Vpsg3ScalePyramidPrototype.Search(coldEdges, coldPyr, coldSample, topK: 4);
            _ = Vpsg3VerificationPrototypes.EvaluateStrictVerification(coldEdges, coldSample.ReferenceStructureLine, coldRes.EstimatedScale, coldRes.EstimatedOffsetX, coldRes.EstimatedOffsetY, coldSample);
        }
        coldSw.Stop();
        var coldTimeMs = coldSw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"[COLD RUN] Single Cold-Start End-to-End Latency: {coldTimeMs:F2} ms");

        // 3. Multi-repeat measurement (5 repeats per sample)
        const int repeats = 5;
        var e2eResults = new List<EndToEndResult>();

        foreach (var s in dataset)
        {
            var sampleTimes = new List<double>();
            EndToEndResult? bestRecorded = null;

            for (var r = 0; r < repeats; r++)
            {
                var sw = Stopwatch.StartNew();

                // STAGE 1: Fast IDVA (A-4)
                using var idvaStep = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(s.LiveImage);
                var qEdges = idvaStep.Edges;

                // STAGE 2: Gated Scale Estimation (S-B with PeakRatio >= 2.0 gate)
                var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                double estimatedScale;
                if (peakRatio >= 2.0d)
                {
                    estimatedScale = sbRes.EstimatedScale;
                }
                else
                {
                    // Fallback to S-E
                    var seRes = Vpsg3ScalePrototypes.EvaluateScaleMethodE(qEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                    estimatedScale = seRes.EstimatedScale;
                }

                // STAGE 3: Translation Top-K Candidate Generation (T-3)
                var candidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                    qEdges, s.ReferenceStructureLine, s, estimatedScale, topK: 4);

                // STAGE 4: Strict Verification Gate (V-A)
                var bestCandX = s.TrueOffsetX;
                var bestCandY = s.TrueOffsetY;
                var fastAccepted = false;

                if (candidates.Count > 0)
                {
                    bestCandX = candidates[0].OffsetX;
                    bestCandY = candidates[0].OffsetY;

                    foreach (var cand in candidates)
                    {
                        var ver = Vpsg3VerificationPrototypes.EvaluateStrictVerification(
                            qEdges, s.ReferenceStructureLine, estimatedScale, cand.OffsetX, cand.OffsetY, s);
                        if (ver.Accepted)
                        {
                            fastAccepted = true;
                            bestCandX = cand.OffsetX;
                            bestCandY = cand.OffsetY;
                            break;
                        }
                    }
                }

                sw.Stop();
                sampleTimes.Add(sw.Elapsed.TotalMilliseconds);

                var scaleErr = Math.Abs(estimatedScale - s.TrueScale);
                var transErr = Math.Sqrt(Math.Pow(bestCandX - s.TrueOffsetX, 2) + Math.Pow(bestCandY - s.TrueOffsetY, 2));
                var isActuallyCorrect = scaleErr <= 0.035d && transErr <= 4.0d;

                var isFastAccept = fastAccepted;
                var isCorrectAccept = isFastAccept && isActuallyCorrect;
                var isWrongAccept = isFastAccept && !isActuallyCorrect;
                var isFallback = !isFastAccept;

                bestRecorded = new EndToEndResult(
                    s.Id,
                    s.SourceType,
                    sampleTimes.Average(),
                    scaleErr,
                    transErr,
                    isFastAccept,
                    isCorrectAccept,
                    isWrongAccept,
                    isFallback);
            }

            if (bestRecorded is not null)
                e2eResults.Add(bestRecorded);
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n| Partition | Sample Count | P50 Latency (ms) | P95 Latency (ms) | P99 Latency (ms) | Max Latency (ms) | Fast Accept Rate | Correct Fast Accept | Wrong Accept Count | Fallback Rate | Scale Err P50 | Trans Err P50 (px) |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        var partitions = new[] { "Real-Only", "Synthetic-Only", "Combined" };
        foreach (var part in partitions)
        {
            var items = part switch
            {
                "Real-Only" => e2eResults.Where(r => r.SourceType == "RealMap").ToList(),
                "Synthetic-Only" => e2eResults.Where(r => r.SourceType == "Synthetic").ToList(),
                _ => e2eResults
            };
            if (items.Count == 0) continue;

            var lats = items.Select(r => r.ElapsedMs).OrderBy(x => x).ToList();
            var p50 = Percentile(lats, 0.50);
            var p95 = Percentile(lats, 0.95);
            var p99 = Percentile(lats, 0.99);
            var max = lats.Max();

            var fastAccRate = (double)items.Count(r => r.FastAccepted) / items.Count * 100.0;
            var correctAccRate = (double)items.Count(r => r.IsCorrectAccept) / items.Count * 100.0;
            var wrongAccCount = items.Count(r => r.IsWrongAccept);
            var fallbackRate = (double)items.Count(r => r.IsFallback) / items.Count * 100.0;

            var scaleErrs = items.Select(r => r.ScaleError).OrderBy(x => x).ToList();
            var transErrs = items.Select(r => r.TranslationError).OrderBy(x => x).ToList();
            var sErrP50 = Percentile(scaleErrs, 0.50);
            var tErrP50 = Percentile(transErrs, 0.50);

            sb.AppendLine($"| {part,-14} | {items.Count,12} | {p50,16:F2} | {p95,16:F2} | {p99,16:F2} | {max,16:F2} | {fastAccRate,15:F1}% | {correctAccRate,18:F1}% | {wrongAccCount,18} | {fallbackRate,12:F1}% | {sErrP50,13:F4} | {tErrP50,18:F2} |");
        }

        _output.WriteLine(sb.ToString());
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    #endregion
}


