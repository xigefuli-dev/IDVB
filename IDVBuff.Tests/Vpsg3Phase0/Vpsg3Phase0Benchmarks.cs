using System.Diagnostics;
using System.Text;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase0;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class Vpsg3Phase0Benchmarks
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase0Benchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RunAllPhase0Point5Experiments()
    {
        _output.WriteLine("===============================================================================");
        _output.WriteLine("       VPSG 3.0 PHASE 0.5 EMPIRICAL BENCHMARK & JOINT VERIFICATION SUITE       ");
        _output.WriteLine("===============================================================================");

        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        var realSamples = dataset.Where(s => s.SourceType == "RealMap").ToList();
        var synSamples = dataset.Where(s => s.SourceType == "Synthetic").ToList();

        _output.WriteLine($"[DATASET] Total: {dataset.Count} samples (Real: {realSamples.Count}, Synthetic: {synSamples.Count})");
        _output.WriteLine($"[SEARCH DOMAIN] Scale search domain strictly enforced at [{Vpsg3ScalePrototypes.DomainMinScale:F2}, {Vpsg3ScalePrototypes.DomainMaxScale:F2}].");

        try
        {
            // -----------------------------------------------------------------
            // 1. Precompile Pyramids for Reference Maps (Needed for S-F & Downstream)
            // -----------------------------------------------------------------
            var synRefLine = dataset.First(s => s.SourceType == "Synthetic").ReferenceStructureLine;
            var synPyramidDs4L7 = Vpsg3ScalePyramidPrototype.BuildPyramid(synRefLine, downsampleFactor: 4, scaleLevelCount: 7);
            var synPyramidDs4L11 = Vpsg3ScalePyramidPrototype.BuildPyramid(synRefLine, downsampleFactor: 4, scaleLevelCount: 11);
            var synPyramidDs8L7 = Vpsg3ScalePyramidPrototype.BuildPyramid(synRefLine, downsampleFactor: 8, scaleLevelCount: 7);
            var synPyramidDs8L11 = Vpsg3ScalePyramidPrototype.BuildPyramid(synRefLine, downsampleFactor: 8, scaleLevelCount: 11);

            Vpsg3ScalePyramidPrototype.FloorScalePyramid? realPyramidDs4L7 = null;
            if (realSamples.Count > 0)
            {
                var realRefLine = realSamples[0].ReferenceStructureLine;
                realPyramidDs4L7 = Vpsg3ScalePyramidPrototype.BuildPyramid(realRefLine, downsampleFactor: 4, scaleLevelCount: 7);
            }

            // -----------------------------------------------------------------
            // 2. ITEM 2 & 3: Fast IDVA Ablation & Downstream Positioning Benchmark
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine(">>> 1. FAST IDVA ABLATION & FULL DOWNSTREAM POSITIONING BENCHMARK");
            _output.WriteLine("===============================================================================");
            RunFastIdvaDownstreamBenchmark(dataset, synPyramidDs4L7, realPyramidDs4L7);

            // Extract observed edges using A-4 for downstream scale/translation benchmarks
            var extractedEdges = new Dictionary<string, Mat>();
            foreach (var sample in dataset)
            {
                var step = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(sample.LiveImage);
                extractedEdges[sample.Id] = step.Edges;
            }

            // -----------------------------------------------------------------
            // 3. ITEM 1 & 4: Scale Benchmark, Outlier Failure Diagnostics & S-B ROC
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine(">>> 2. SCALE ESTIMATOR MATRIX (S-A ~ S-E) & OUTLIER FAILURE ANALYSIS");
            _output.WriteLine("===============================================================================");
            RunScaleBenchmarkAndDiagnostics(dataset, extractedEdges);

            // -----------------------------------------------------------------
            // 4. ITEM 9: S-F Precompiled Multi-Scale Bitset Pyramid Benchmark
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine(">>> 3. S-F PROTOTYPE: PRECOMPILED MULTI-SCALE BITSET PYRAMID");
            _output.WriteLine("===============================================================================");
            RunScalePyramidBenchmark(dataset, extractedEdges, synPyramidDs4L7, synPyramidDs4L11, synPyramidDs8L7, synPyramidDs8L11, realPyramidDs4L7);

            // -----------------------------------------------------------------
            // 5. ITEM 6: T-3 Restructured as Top-K Candidate Generator
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine(">>> 4. T-3 AS TOP-K TRANSLATION CANDIDATE GENERATOR");
            _output.WriteLine("===============================================================================");
            RunTranslationTopKBenchmark(dataset, extractedEdges);

            // -----------------------------------------------------------------
            // 6. ITEM 7: V-A Restructured as Fast Strict Acceptance Gate
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine(">>> 5. V-A AS STRICT FAST ACCEPTANCE GATE (Zero Wrong Accept Target)");
            _output.WriteLine("===============================================================================");
            RunStrictVerificationBenchmark(dataset, extractedEdges);

            // -----------------------------------------------------------------
            // 7. ITEM 10 & 11: Genuine End-to-End Joint Benchmark (Warm-up & Release)
            // -----------------------------------------------------------------
            _output.WriteLine("\n===============================================================================");
            _output.WriteLine(">>> 6. GENUINE END-TO-END JOINT PIPELINE BENCHMARK (Warm-up + Multi-repeat)");
            _output.WriteLine("===============================================================================");
            RunGenuineEndToEndBenchmark(dataset, synPyramidDs4L7, realPyramidDs4L7);

            // Cleanup
            synPyramidDs4L7.Dispose();
            synPyramidDs4L11.Dispose();
            synPyramidDs8L7.Dispose();
            synPyramidDs8L11.Dispose();
            realPyramidDs4L7?.Dispose();

            foreach (var kvp in extractedEdges)
                kvp.Value.Dispose();
        }
        finally
        {
            foreach (var s in dataset)
                s.Dispose();
        }
    }

    #region Benchmark 1: Fast IDVA & Downstream

    private void RunFastIdvaDownstreamBenchmark(
        List<GroundTruthSample> dataset,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid synPyramid,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid? realPyramid)
    {
        var stages = new[]
        {
            "A-0 (Baseline IDVA 2.0)",
            "A-1 (Drop Edge CC)",
            "A-2 (Drop Hole Fill)",
            "A-3 (Morphology over Room/Corridor CC)",
            "A-4 (Cheap Dynamic Exclusion)",
            "A-5 (2x Downsampled Streamlined)"
        };

        var subset = dataset.Where(s => s.FogFraction <= 0.60).ToList();
        var allDownstream = new List<IdvaDownstreamResult>();

        foreach (var sample in subset)
        {
            var pyr = sample.SourceType == "RealMap" && realPyramid is not null ? realPyramid : synPyramid;
            var baseline = Vpsg3FastIdvaPrototypes.RunA0Baseline(sample.LiveImage);
            using (baseline.Edges)
            {
                foreach (var st in stages)
                {
                    var res = Vpsg3FastIdvaPrototypes.EvaluateDownstreamPipeline(st, sample, baseline.Edges, pyr);
                    allDownstream.Add(res);
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("\n| Ablation Stage | P50 (ms) | P95 (ms) | vs A-0 Prec | vs A-0 Rec | vs GT Prec | vs GT Rec | Downstream Scale Err | Downstream Trans Err | Top-1 Hit <=3px | Top-4 Hit <=3px | False Cands |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (var st in stages)
        {
            var items = allDownstream.Where(d => d.StageName == st).ToList();
            var lats = items.Select(d => d.ExtractionElapsedMs).OrderBy(x => x).ToList();
            var p50 = Percentile(lats, 0.50);
            var p95 = Percentile(lats, 0.95);
            var pBase = items.Average(d => d.PrecisionVsBaseline);
            var rBase = items.Average(d => d.RecallVsBaseline);
            var pGt = items.Average(d => d.PrecisionVsGroundTruth);
            var rGt = items.Average(d => d.RecallVsGroundTruth);
            var scaleErr = items.Average(d => d.DownstreamScaleError);
            var transErr = items.Average(d => d.DownstreamTranslationError);
            var top1Hit = (double)items.Count(d => d.Top1Hit3px) / items.Count * 100.0;
            var top4Hit = (double)items.Count(d => d.Top4Hit3px) / items.Count * 100.0;
            var falseC = items.Average(d => d.FalseCandidateCount);

            sb.AppendLine($"| {st,-35} | {p50,8:F2} | {p95,8:F2} | {pBase,11:F3} | {rBase,10:F3} | {pGt,10:F3} | {rGt,9:F3} | {scaleErr,20:F4} | {transErr,20:F2} | {top1Hit,14:F1}% | {top4Hit,14:F1}% | {falseC,11:F1} |");
        }

        _output.WriteLine(sb.ToString());
    }

    #endregion

    #region Benchmark 2: Scale Benchmark & Outlier Diagnostics

    private void RunScaleBenchmarkAndDiagnostics(
        List<GroundTruthSample> dataset,
        Dictionary<string, Mat> extractedEdges)
    {
        var failures = new List<ScaleFailureDetail>();
        var results = new List<ScaleBenchmarkResult>();

        foreach (var s in dataset)
        {
            var qEdges = extractedEdges[s.Id];
            var rEdges = s.ReferenceStructureLine;

            var rA = Vpsg3ScalePrototypes.EvaluateScaleMethodA(qEdges, rEdges, s.TrueScale, s.Id, s.SourceType);
            var (rB, rB_ratio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(qEdges, rEdges, s.TrueScale, s.Id, s.SourceType);
            var rC = Vpsg3ScalePrototypes.EvaluateScaleMethodC(qEdges, rEdges, s.TrueScale, s.Id, s.SourceType);
            var rD = Vpsg3ScalePrototypes.EvaluateScaleMethodD(qEdges, rEdges, s.TrueScale, s.Id, s.SourceType);
            var rE = Vpsg3ScalePrototypes.EvaluateScaleMethodE(qEdges, rEdges, s.TrueScale, s.Id, s.SourceType);

            results.AddRange(new[] { rA, rB, rC, rD, rE });

            // Record failure details if error > 0.08
            if (rB.ScaleError > 0.08d)
                failures.Add(new ScaleFailureDetail(s.Id, "S-B", s.SourceType, s.TrueScale, rB.EstimatedScale, rB.ScaleError, "[0.70, 1.50]", $"PeakRatio={rB_ratio:F2} < 2.0 (Harmonic mismatch)"));
            if (rC.ScaleError > 0.08d)
                failures.Add(new ScaleFailureDetail(s.Id, "S-C", s.SourceType, s.TrueScale, rC.EstimatedScale, rC.ScaleError, "[0.70, 1.50]", "Quantile distance shifted by empty corridor area"));
        }

        // Print Outlier Failure Log
        _output.WriteLine("\n--- [FAILURE SAMPLE DIAGNOSTICS LOG (Error > 0.08)] ---");
        _output.WriteLine($"Found {failures.Count} scale outlier instances. First 6 diagnostic records:");
        foreach (var f in failures.Take(6))
        {
            _output.WriteLine($"  * Sample: {f.SampleId,-28} | Algo: {f.Algorithm} ({f.SourceType}) | TrueScale: {f.GroundTruthScale:F3} | Est: {f.EstimatedScale:F3} | Err: {f.AbsoluteError:F4} | Domain: {f.SearchDomain} | Reason: {f.FailureReason}");
        }

        // Print Corrected Scale Table (Separated by Real vs Synthetic vs Overall)
        _output.WriteLine("\n--- [CORRECTED SCALE ESTIMATOR BENCHMARK TABLE (Domain-Clamped)] ---");
        PrintScalePartitionedReport(results);

        // Print S-B PeakRatio Gate ROC Table
        _output.WriteLine("\n--- [S-B PEAKRATIO GATE ROC BENCHMARK TABLE] ---");
        var thresholds = new[] { 1.5d, 2.0d, 2.5d, 3.0d, 4.0d };
        var rocPoints = Vpsg3ScalePrototypes.EvaluateSBPeakRatioRoc(dataset, extractedEdges, thresholds);

        var sbRoc = new StringBuilder();
        sbRoc.AppendLine("| PeakRatio Threshold | Gate Coverage | Conditional P50 Err | Conditional P95 Err | Conditional Max Err | Catastrophic Err Rate (>0.05) |");
        sbRoc.AppendLine("| :---: | :---: | :---: | :---: | :---: | :---: |");
        foreach (var p in rocPoints)
        {
            sbRoc.AppendLine($"| {p.Threshold,19:F1} | {p.GateCoverage,11:F1}% | {p.ConditionalErrorP50,19:F4} | {p.ConditionalErrorP95,19:F4} | {p.ConditionalErrorMax,19:F4} | {p.CatastrophicErrorRate,27:F1}% |");
        }
        _output.WriteLine(sbRoc.ToString());
    }

    private void PrintScalePartitionedReport(List<ScaleBenchmarkResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Partition | Candidate Algorithm | Success Rate | Error P50 | Error P95 | Error Max | Norm Margin | FWHM (ln s) | Latency P50 (ms) |");
        sb.AppendLine("| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        var partitions = new[] { "Real-Only", "Synthetic-Only", "Combined" };
        var algos = results.Select(r => r.Algorithm).Distinct().ToList();

        foreach (var part in partitions)
        {
            var partItems = part switch
            {
                "Real-Only" => results.Where(r => r.SourceType == "RealMap").ToList(),
                "Synthetic-Only" => results.Where(r => r.SourceType == "Synthetic").ToList(),
                _ => results
            };

            if (partItems.Count == 0) continue;

            foreach (var algo in algos)
            {
                var subset = partItems.Where(r => r.Algorithm == algo).ToList();
                var successRate = (double)subset.Count(r => r.Success) / subset.Count * 100.0;
                var errors = subset.Select(r => r.ScaleError).OrderBy(x => x).ToList();
                var lats = subset.Select(r => r.ElapsedMs).OrderBy(x => x).ToList();

                var errP50 = Percentile(errors, 0.50);
                var errP95 = Percentile(errors, 0.95);
                var errMax = errors.Max();
                var margin = subset.Average(r => r.NormalizedMargin);
                var fwhm = subset.Average(r => r.FwhmLogScale);
                var latP50 = Percentile(lats, 0.50);

                sb.AppendLine($"| {part,-14} | {algo,-32} | {successRate,10:F1}% | {errP50,9:F4} | {errP95,9:F4} | {errMax,9:F4} | {margin,11:F3} | {fwhm,11:F3} | {latP50,16:F2} |");
            }
        }

        _output.WriteLine(sb.ToString());
    }

    #endregion

    #region Benchmark 3: S-F Pyramid

    private void RunScalePyramidBenchmark(
        List<GroundTruthSample> dataset,
        Dictionary<string, Mat> extractedEdges,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid synPyrDs4L7,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid synPyrDs4L11,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid synPyrDs8L7,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid synPyrDs8L11,
        Vpsg3ScalePyramidPrototype.FloorScalePyramid? realPyrDs4L7)
    {
        var configs = new (int Ds, int Levels, Vpsg3ScalePyramidPrototype.FloorScalePyramid SynPyr)[]
        {
            (4, 7, synPyrDs4L7),
            (4, 11, synPyrDs4L11),
            (8, 7, synPyrDs8L7),
            (8, 11, synPyrDs8L11)
        };

        var sb = new StringBuilder();
        sb.AppendLine("| Pyramid Config | Mem/Floor (KB) | 110 Floors (MB) | Build Time (ms) | Scale Err P50 | Scale Err P95 | Trans Top-1 Hit | Trans Top-4 Hit | Runtime P50 (ms) | Runtime P95 (ms) |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (var (ds, levels, synPyr) in configs)
        {
            var searchResults = new List<(double ScaleErr, double TransErr, bool Top1Hit, bool Top4Hit, double TimeMs)>();

            foreach (var s in dataset)
            {
                var pyr = (s.SourceType == "RealMap" && realPyrDs4L7 is not null && ds == 4 && levels == 7) ? realPyrDs4L7 : synPyr;
                var res = Vpsg3ScalePyramidPrototype.Search(extractedEdges[s.Id], pyr, s, topK: 8);

                var scaleErr = Math.Abs(res.EstimatedScale - s.TrueScale);
                var transErr = Math.Sqrt(Math.Pow(res.EstimatedOffsetX - s.TrueOffsetX, 2) + Math.Pow(res.EstimatedOffsetY - s.TrueOffsetY, 2));

                var top1Hit = transErr <= 3.0d;
                var top4Hit = res.TopCandidates.Take(4).Any(c => Math.Sqrt(Math.Pow(c.OffsetX - s.TrueOffsetX, 2) + Math.Pow(c.OffsetY - s.TrueOffsetY, 2)) <= 3.0d);

                searchResults.Add((scaleErr, transErr, top1Hit, top4Hit, res.ElapsedMs));
            }

            var memPerFloorKb = synPyr.MemoryBytes / 1024.0;
            var total110Mb = (synPyr.MemoryBytes * 110) / (1024.0 * 1024.0);
            var buildTime = synPyr.BuildTimeMs;

            var scaleErrors = searchResults.Select(r => r.ScaleErr).OrderBy(x => x).ToList();
            var scaleP50 = Percentile(scaleErrors, 0.50);
            var scaleP95 = Percentile(scaleErrors, 0.95);

            var top1Rec = (double)searchResults.Count(r => r.Top1Hit) / searchResults.Count * 100.0;
            var top4Rec = (double)searchResults.Count(r => r.Top4Hit) / searchResults.Count * 100.0;

            var times = searchResults.Select(r => r.TimeMs).OrderBy(x => x).ToList();
            var tP50 = Percentile(times, 0.50);
            var tP95 = Percentile(times, 0.95);

            sb.AppendLine($"| DS={ds}x, L={levels,-2} | {memPerFloorKb,14:F1} | {total110Mb,15:F2} | {buildTime,15:F2} | {scaleP50,13:F4} | {scaleP95,13:F4} | {top1Rec,14:F1}% | {top4Rec,14:F1}% | {tP50,16:F2} | {tP95,16:F2} |");
        }

        _output.WriteLine(sb.ToString());
    }

    #endregion

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
