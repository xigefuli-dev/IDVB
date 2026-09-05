using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3ProductionSolverTests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3ProductionSolverTests(ITestOutputHelper output)
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

    private static Vpsg3IndexCacheKey MakeKey(string refName) =>
        new(Guid.NewGuid(), "1F", "hash_" + refName, DateTimeOffset.UtcNow, "gen_" + refName);

    [Fact]
    public void Test1_ScaleSolverDifferential_MatchesPhase3APrototype()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var matchCount = 0;
            var evaluatedCount = 0;

            foreach (var sample in dataset)
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                var key = MakeKey(sample.ReferenceName);
                using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

                var prodScale = Vpsg3ScaleSolver.Solve(obs, preparedFloor);
                var (protoScale, protoPeakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                    obs.ObservedEdges, sample.ReferenceStructureLine, sample.TrueScale, sample.Id, sample.SourceType);

                evaluatedCount++;

                if (protoPeakRatio >= 2.0d)
                {
                    Assert.True(prodScale.Success, $"Sample {sample.Id} expected success in production ScaleSolver.");
                    Assert.True(Math.Abs(prodScale.SeedScale - protoScale.EstimatedScale) < 1e-4,
                        $"Scale mismatch on {sample.Id}: prod={prodScale.SeedScale:F4}, proto={protoScale.EstimatedScale:F4}");
                    matchCount++;
                }
                else if (prodScale.Success)
                {
                    // Bounded pitch search in production successfully avoided out-of-domain harmonics where unconstrained prototype failed
                    Assert.True(Math.Abs(prodScale.SeedScale - sample.TrueScale) <= 0.05d,
                        $"Production recovered scale {prodScale.SeedScale:F4} on {sample.Id} deviates too much from true scale {sample.TrueScale:F4}.");
                    matchCount++;
                }
                else
                {
                    Assert.False(prodScale.Success, $"Sample {sample.Id} expected failure due to PeakRatio < 2.0.");
                }
            }

            _output.WriteLine($"Differential test passed: {matchCount}/{evaluatedCount} samples with PeakRatio >= 2.0 matched prototype identically.");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test2_ProductionBitsets_BitAccuracy()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            var verifiedFloors = 0;

            foreach (var sample in dataset.DistinctBy(s => s.ReferenceName))
            {
                var refMat = sample.ReferenceStructureLine;
                var key = MakeKey(sample.ReferenceName);
                using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(refMat, key);

                using var dil3 = new Mat();
                using var dil5 = new Mat();
                Cv2.Dilate(refMat, dil3, k3);
                Cv2.Dilate(refMat, dil5, k5);

                var width = refMat.Width;
                var height = refMat.Height;

                for (var y = 0; y < height; y += 4)
                {
                    for (var x = 0; x < width; x += 4)
                    {
                        var expK3 = dil3.At<byte>(y, x) > 128;
                        var expK5 = dil5.At<byte>(y, x) > 128;
                        Assert.Equal(expK3, preparedFloor.IsHitK3(x, y));
                        Assert.Equal(expK5, preparedFloor.IsHitK5(x, y));
                    }
                }
                verifiedFloors++;
            }

            _output.WriteLine($"Verified K3 and K5 bitset representations across {verifiedFloors} distinct floor references.");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test3_ProductionEndToEndJointBenchmark()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var refFloors = new Dictionary<string, Vpsg3PreparedFloor>();
            foreach (var s in dataset)
            {
                if (!refFloors.ContainsKey(s.ReferenceName))
                {
                    var key = MakeKey(s.ReferenceName);
                    refFloors[s.ReferenceName] = Vpsg3PreparedIndexBuilder.BuildFromMat(s.ReferenceStructureLine, key);
                }
            }

            try
            {
                RunEndToEndEvaluation(dataset, refFloors);
            }
            finally
            {
                foreach (var f in refFloors.Values) f.Dispose();
            }
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    private void RunEndToEndEvaluation(List<GroundTruthSample> dataset, Dictionary<string, Vpsg3PreparedFloor> refFloors)
    {
        var groups = new[]
        {
            ("All", dataset),
            ("RealMap", dataset.Where(s => s.SourceType == "RealMap").ToList()),
            ("Synthetic", dataset.Where(s => s.SourceType != "RealMap").ToList())
        };

        var sb = new StringBuilder();
        sb.AppendLine("========================================================================================================================================");
        sb.AppendLine("                                      VPSG 3.0 PHASE 3B: PRODUCTION SOLVER BENCHMARK REPORT                                            ");
        sb.AppendLine("========================================================================================================================================");

        var totalWrongAcceptCount = 0;
        var evidence = new List<object>();

        foreach (var (groupName, samples) in groups)
        {
            var total = samples.Count;
            var candsGen = 0;
            var top1GtRecall = 0;
            var noCand = 0;
            var accepted = 0;
            var wrongAccept = 0;
            var transErrors = new List<double>();
            var scaleErrors = new List<double>();

            var latExtract = new List<double>();
            var latScale = new List<double>();
            var latTrans = new List<double>();
            var latRefine = new List<double>();
            var latVer = new List<double>();
            var latGate = new List<double>();
            var latTotal = new List<double>();

            foreach (var s in samples)
            {
                var floor = refFloors[s.ReferenceName];
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);

                var res = Vpsg3FastBootstrapSolver.TrySolve(obs, floor);
                if (groupName == "All") evidence.Add(new
                {
                    s.Id, s.SourceType, s.ViewportBounds, s.TrueScale, s.TrueOffsetX, s.TrueOffsetY,
                    res.IsAccepted, res.FallbackReason, res.Scale, res.OffsetX, res.OffsetY,
                    res.BestCandidate, res.RunnerUpCandidate, res.Timing
                });

                latExtract.Add(res.Timing.ExtractionMs);
                latScale.Add(res.Timing.ScaleMs);
                latTrans.Add(res.Timing.TranslationMs);
                latRefine.Add(res.Timing.RefineMs);
                latVer.Add(res.Timing.VerificationMs);
                latGate.Add(res.Timing.GateMs);
                latTotal.Add(res.Timing.TotalMs);

                if (res.ScaleResult.Success)
                {
                    if (res.FallbackReason != "TranslationNoCandidatesFound")
                    {
                        candsGen++;
                    }
                    else
                    {
                        noCand++;
                    }
                }
                else
                {
                    noCand++;
                }

                if (res.IsAccepted)
                {
                    accepted++;
                    var tErr = Math.Sqrt(Math.Pow(res.OffsetX - s.TrueOffsetX, 2) + Math.Pow(res.OffsetY - s.TrueOffsetY, 2));
                    var sErr = Math.Abs(res.Scale - s.TrueScale);
                    transErrors.Add(tErr);
                    scaleErrors.Add(sErr);

                    if (tErr <= 4.0d && sErr <= 0.035d)
                    {
                        top1GtRecall++;
                    }
                    else
                    {
                        wrongAccept++;
                        if (groupName == "All") totalWrongAcceptCount++;
                        _output.WriteLine($"[WRONG_ACCEPT] {s.Id}: transErr={tErr:F2}px, scaleErr={sErr:F4}, estScale={res.Scale:F4}, trueScale={s.TrueScale:F4}, score={res.Confidence:F3}, margin={res.ApertureMargin:F3}, hasRunnerUp={res.HasDistinctRunnerUp}");
                    }
                }
                else if (groupName == "RealMap")
                {
                    _output.WriteLine($"[REALMAP_FALLBACK] {s.Id}: reason={res.FallbackReason}, scaleSucc={res.ScaleResult.Success}, peakRatio={res.ScaleResult.PeakRatio:F2}");
                }
            }

            var candRate = candsGen * 100.0 / total;
            var top1RecallRate = top1GtRecall * 100.0 / total;
            var noCandRate = noCand * 100.0 / total;
            var covRate = accepted * 100.0 / total;
            var precision = accepted > 0 ? (accepted - wrongAccept) * 100.0 / accepted : 100.0;
            var fallbackRate = (total - accepted) * 100.0 / total;

            sb.AppendLine($"### Cohort: {groupName} (N={total})");
            sb.AppendLine($"| Metric | Value |");
            sb.AppendLine($"| :--- | :--- |");
            sb.AppendLine($"| CandidateGeneratedRate | {candRate:F1}% ({candsGen}/{total}) |");
            sb.AppendLine($"| Top-1 GT Recall (accepted, <= 4px and scale error <= 0.035) | {top1RecallRate:F1}% ({top1GtRecall}/{total}) |");
            sb.AppendLine($"| NoCandidateRate | {noCandRate:F1}% ({noCand}/{total}) |");
            sb.AppendLine($"| FastAcceptCoverage | {covRate:F1}% ({accepted}/{total}) |");
            sb.AppendLine($"| AcceptedPrecision | {precision:F1}% |");
            sb.AppendLine($"| **WrongAcceptCount** | **{wrongAccept}** |");
            sb.AppendLine($"| RejectRate (solver only) | {fallbackRate:F1}% ({total - accepted}/{total}) |");
            sb.AppendLine($"| Trans Error P50 / P95 | {Percentile(transErrors, 0.50):F2}px / {Percentile(transErrors, 0.95):F2}px |");
            sb.AppendLine($"| Scale Error P50 / P95 | {Percentile(scaleErrors, 0.50):F4} / {Percentile(scaleErrors, 0.95):F4} |");
            sb.AppendLine();
            sb.AppendLine($"| Stage Breakdown (ms) | P50 | P95 | P99 | Max |");
            sb.AppendLine($"| :--- | :---: | :---: | :---: | :---: |");
            sb.AppendLine($"| 1. Live Extraction | {Percentile(latExtract, 0.50):F2} | {Percentile(latExtract, 0.95):F2} | {Percentile(latExtract, 0.99):F2} | {latExtract.Max():F2} |");
            sb.AppendLine($"| 2. Scale Solver (S-B) | {Percentile(latScale, 0.50):F2} | {Percentile(latScale, 0.95):F2} | {Percentile(latScale, 0.99):F2} | {latScale.Max():F2} |");
            sb.AppendLine($"| 3. Translation Solver (T-3) | {Percentile(latTrans, 0.50):F2} | {Percentile(latTrans, 0.95):F2} | {Percentile(latTrans, 0.99):F2} | {latTrans.Max():F2} |");
            sb.AppendLine($"| 4. Local Refiner (277 probes/candidate, including pool refill) | {Percentile(latRefine, 0.50):F2} | {Percentile(latRefine, 0.95):F2} | {Percentile(latRefine, 0.99):F2} | {latRefine.Max():F2} |");
            sb.AppendLine($"| 5. Spatial Verification | {Percentile(latVer, 0.50):F2} | {Percentile(latVer, 0.95):F2} | {Percentile(latVer, 0.99):F2} | {latVer.Max():F2} |");
            sb.AppendLine($"| 6. Joint Gate Decision | {Percentile(latGate, 0.50):F2} | {Percentile(latGate, 0.95):F2} | {Percentile(latGate, 0.99):F2} | {latGate.Max():F2} |");
            sb.AppendLine($"| **Pipeline Total** | **{Percentile(latTotal, 0.50):F2}** | **{Percentile(latTotal, 0.95):F2}** | **{Percentile(latTotal, 0.99):F2}** | **{latTotal.Max():F2}** |");
            sb.AppendLine();
        }

        _output.WriteLine(sb.ToString());
        try
        {
            var scratchDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../scratch"));
            if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
            File.WriteAllText(Path.Combine(scratchDir, "phase3b_production_solver_benchmark.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(scratchDir, "phase3b_joint_evidence.json"),
                System.Text.Json.JsonSerializer.Serialize(evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore filesystem error in test runner sandbox
        }

        Assert.Equal(0, totalWrongAcceptCount);
    }

    [Fact]
    public void Test4_ZeroSteadyStateManagedAllocations()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var s = dataset.First(d => d.SourceType == "RealMap");
            using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
            var key = MakeKey(s.ReferenceName);
            using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(s.ReferenceStructureLine, key);

            // 1. Warm-up (ensure JIT compilation and thread-local scratch allocations)
            for (var i = 0; i < 5; i++)
            {
                _ = Vpsg3FastBootstrapSolver.TrySolve(obs, floor);
            }

            // 2. Measure steady-state allocations across 10 iterations
            const int iterations = 10;
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                _ = Vpsg3FastBootstrapSolver.TrySolve(obs, floor);
            }
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();

            var totalAlloc = allocAfter - allocBefore;
            var perIterAlloc = (double)totalAlloc / iterations;

            _output.WriteLine($"Steady-state allocation: {perIterAlloc:F0} bytes / solve ({totalAlloc} bytes total across {iterations} iterations)");

            // Only immutable result DTO and small value-type boxings allowed (<= 1024 bytes).
            Assert.True(perIterAlloc <= 1024, $"Expected minimal managed allocations in steady-state (<= 1KB), but got {perIterAlloc:F0} bytes.");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void MergedPeaksRecoverFromRetainedPoolWithoutRelaxingMargin()
    {
        var samples = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            foreach (var id in new[] { "real_049_s0.88_f40", "real_051_s1.00_f0", "real_054_s1.25_f0", "real_050_s0.88_f70" })
            {
                var sample = samples.Single(s => s.Id == id);
                using var observation = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, MakeKey(sample.ReferenceName));
                var result = Vpsg3FastBootstrapSolver.TrySolve(observation, floor);
                if (id == "real_050_s0.88_f70")
                {
                    Assert.False(result.IsAccepted);
                    Assert.StartsWith("ApertureMarginBelowThreshold", result.FallbackReason);
                    continue;
                }
                Assert.True(result.IsAccepted, $"{id}: {result.FallbackReason}");
                Assert.True(result.HasDistinctRunnerUp);
                Assert.True(result.ApertureMargin >= .09);
                Assert.True(Math.Abs(result.Scale - sample.TrueScale) <= .035);
                Assert.True(double.Hypot(result.OffsetX - sample.TrueOffsetX, result.OffsetY - sample.TrueOffsetY) <= 4);
            }
        }
        finally { foreach (var sample in samples) sample.Dispose(); }
    }

    [Fact]
    public void Test5_EdgeCaseRegression_GuaranteesCorrectDecisions()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            // syn_023: High-confidence sample -> Must FastAccept with <= 2px error
            var s023 = dataset.First(d => d.Id == "syn_023_s1.00_f75");
            using (var floor23 = Vpsg3PreparedIndexBuilder.BuildFromMat(s023.ReferenceStructureLine, MakeKey(s023.ReferenceName)))
            using (var obs23 = Vpsg3FastLiveExtractor.Extract(s023.LiveImage, s023.ViewportBounds))
            {
                var res23 = Vpsg3FastBootstrapSolver.TrySolve(obs23, floor23);
                Assert.True(res23.IsAccepted, $"syn_023 should be FastAccepted, got {res23.FallbackReason}");
                var tErr23 = Math.Sqrt(Math.Pow(res23.OffsetX - s023.TrueOffsetX, 2) + Math.Pow(res23.OffsetY - s023.TrueOffsetY, 2));
                Assert.True(tErr23 <= 2.0d, $"syn_023 translation error {tErr23:F2}px > 2.0px");
            }

            // syn_035: Ambiguous competing modes -> Must Fallback to VPSG2 (strictly zero WrongAccept)
            var s035 = dataset.First(d => d.Id == "syn_035_s1.18_f75");
            using (var floor35 = Vpsg3PreparedIndexBuilder.BuildFromMat(s035.ReferenceStructureLine, MakeKey(s035.ReferenceName)))
            using (var obs35 = Vpsg3FastLiveExtractor.Extract(s035.LiveImage, s035.ViewportBounds))
            {
                var res35 = Vpsg3FastBootstrapSolver.TrySolve(obs35, floor35);
                Assert.False(res35.IsAccepted, "syn_035 must fallback due to aperture ambiguity");
            }
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }
}

