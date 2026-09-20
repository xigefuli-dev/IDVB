using System.Diagnostics;
using System.Text;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase0;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed partial class Vpsg3Phase0Benchmarks
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

}
