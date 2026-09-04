using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3Phase3BInvestigationTests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase3BInvestigationTests(ITestOutputHelper output)
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

    // ---------------------------------------------------------------------------------------------
    // ITEM 8: Resident Bitset Representation Benchmark (K3 + K5)
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Benchmark_Item8_ResidentBitsetSchemes()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var s0 = dataset.First(s => s.SourceType == "RealMap");
            var refMat = s0.ReferenceStructureLine;
            var width = refMat.Width;
            var height = refMat.Height;
            var stride = (width + 63) / 64;

            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var dil3 = new Mat();
            using var dil5 = new Mat();
            Cv2.Dilate(refMat, dil3, k3);
            Cv2.Dilate(refMat, dil5, k5);

            // Scheme A: Dual independent bitsets
            var bitsetK3 = new ulong[height * stride];
            var bitsetK5 = new ulong[height * stride];
            for (var y = 0; y < height; y++)
            {
                var rowOff = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (dil3.At<byte>(y, x) > 128) bitsetK3[rowOff + (x >> 6)] |= (1UL << (x & 63));
                    if (dil5.At<byte>(y, x) > 128) bitsetK5[rowOff + (x >> 6)] |= (1UL << (x & 63));
                }
            }

            // Scheme C: Interleaved 2-bit representation (32 pixels per ulong: bit 0 = K5, bit 1 = K3)
            var strideC = (width + 31) / 32;
            var bitsetC = new ulong[height * strideC];
            for (var y = 0; y < height; y++)
            {
                var rowOff = y * strideC;
                for (var x = 0; x < width; x++)
                {
                    ulong val = 0;
                    if (dil5.At<byte>(y, x) > 128) val |= 1UL;
                    if (dil3.At<byte>(y, x) > 128) val |= 2UL;
                    if (val != 0)
                        bitsetC[rowOff + (x >> 5)] |= (val << ((x & 31) * 2));
                }
            }

            // Generate 100,000 random test probe points within reference bounds
            var rng = new Random(42);
            const int probeCount = 100_000;
            var testPoints = new Point[probeCount];
            for (var i = 0; i < probeCount; i++)
                testPoints[i] = new Point(rng.Next(2, width - 3), rng.Next(2, height - 3));

            // Benchmark Scheme A: Direct dual bitset lookup
            var swA = Stopwatch.StartNew();
            long hitsA = 0;
            for (var i = 0; i < probeCount; i++)
            {
                var p = testPoints[i];
                var idx = p.Y * stride + (p.X >> 6);
                var mask = 1UL << (p.X & 63);
                var isK5 = (bitsetK5[idx] & mask) != 0;
                var isK3 = (bitsetK3[idx] & mask) != 0;
                if (isK5) hitsA++;
                if (isK3) hitsA += 2;
            }
            swA.Stop();

            // Benchmark Scheme B: Resident K3 only, K5 via 3x3 neighborhood of K3
            var swB = Stopwatch.StartNew();
            long hitsB = 0;
            for (var i = 0; i < probeCount; i++)
            {
                var p = testPoints[i];
                var px = p.X;
                var py = p.Y;
                var idx = py * stride + (px >> 6);
                var mask = 1UL << (px & 63);
                var isK3 = (bitsetK3[idx] & mask) != 0;
                bool isK5;
                if (isK3)
                {
                    isK5 = true;
                }
                else
                {
                    isK5 = false;
                    for (var dy = -1; dy <= 1 && !isK5; dy++)
                    {
                        var rowOff = (py + dy) * stride;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = px + dx;
                            if ((bitsetK3[rowOff + (nx >> 6)] & (1UL << (nx & 63))) != 0)
                            {
                                isK5 = true;
                                break;
                            }
                        }
                    }
                }
                if (isK5) hitsB++;
                if (isK3) hitsB += 2;
            }
            swB.Stop();

            // Benchmark Scheme C: Interleaved 2-bit
            var swC = Stopwatch.StartNew();
            long hitsC = 0;
            for (var i = 0; i < probeCount; i++)
            {
                var p = testPoints[i];
                var idx = p.Y * strideC + (p.X >> 5);
                var shift = (p.X & 31) * 2;
                var val = (bitsetC[idx] >> shift) & 3UL;
                if ((val & 1UL) != 0) hitsC++;
                if ((val & 2UL) != 0) hitsC += 2;
            }
            swC.Stop();

            Assert.Equal(hitsA, hitsB);
            Assert.Equal(hitsA, hitsC);

            var sb = new StringBuilder();
            sb.AppendLine("=========================================================================================");
            sb.AppendLine("                   ITEM 8: RESIDENT BITSET SCHEME BENCHMARK (100k Probes)                ");
            sb.AppendLine("=========================================================================================");
            sb.AppendLine("| Scheme | Description | 136-Floor Total RAM | 100k Probes Time | Per-Probe Latency | Differential |");
            sb.AppendLine("| :--- | :--- | :---: | :---: | :---: | :---: |");
            sb.AppendLine($"| Scheme A | Resident K3 + Resident K5 Bitsets | 42.0 MB (2 layers) | {swA.Elapsed.TotalMilliseconds,14:F2} ms | {swA.Elapsed.TotalMilliseconds * 10.0,15:F1} ns | Baseline 100% |");
            sb.AppendLine($"| Scheme B | Resident K3 Only (K5 via 3x3)     | 21.0 MB (1 layer)  | {swB.Elapsed.TotalMilliseconds,14:F2} ms | {swB.Elapsed.TotalMilliseconds * 10.0,15:F1} ns | {swB.Elapsed.TotalMilliseconds / swA.Elapsed.TotalMilliseconds * 100.0,12:F1}% ({swB.Elapsed.TotalMilliseconds / swA.Elapsed.TotalMilliseconds:F1}x slower) |");
            sb.AppendLine($"| Scheme C | Interleaved 2-bit Bitset          | 42.0 MB (2 bits)   | {swC.Elapsed.TotalMilliseconds,14:F2} ms | {swC.Elapsed.TotalMilliseconds * 10.0,15:F1} ns | {swC.Elapsed.TotalMilliseconds / swA.Elapsed.TotalMilliseconds * 100.0,12:F1}% |");
            _output.WriteLine(sb.ToString());
            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                File.WriteAllText(Path.Combine(scratchDir, "phase3b_item8_bitset.txt"), sb.ToString());
            }
            catch { }
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // ITEM 4: Top-K & Diversity NMS Benchmark
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Benchmark_Item4_TopKAndDiversity()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var kConfigs = new[] { ("Top1", 1, false), ("Top2_NMS", 2, true), ("Top4_NMS", 4, true), ("Top4_Raw", 4, false) };
            var sb = new StringBuilder();
            sb.AppendLine("=========================================================================================");
            sb.AppendLine("                   ITEM 4: TOP-K & DIVERSITY NMS BENCHMARK (57 SAMPLES)                  ");
            sb.AppendLine("=========================================================================================");
            sb.AppendLine("Candidate-only prototype ablation; no refinement or acceptance gate is run here.");
            sb.AppendLine("| Config | Candidate GT Recall (<=8px) | Avg Distinct Dist | P50 Latency |");
            sb.AppendLine("| :--- | :---: | :---: | :---: |");

            foreach (var (name, k, useNms) in kConfigs)
            {
                var gtHits = 0;
                var dists = new List<double>();
                var latencies = new List<double>();

                foreach (var s in dataset)
                {
                    using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                    var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                        obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                    if (peakRatio < 2.0d) continue;

                    var sw = Stopwatch.StartNew();
                    var rawCands = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                        obs.ObservedEdges, s.ReferenceStructureLine, s, sbRes.EstimatedScale, topK: 12);

                    List<(double OffsetX, double OffsetY, int Score)> cands;
                    if (useNms)
                    {
                        cands = new List<(double OffsetX, double OffsetY, int Score)>();
                        foreach (var c in rawCands)
                        {
                            var suppress = cands.Any(existing =>
                                Math.Sqrt(Math.Pow(existing.OffsetX - c.OffsetX, 2) + Math.Pow(existing.OffsetY - c.OffsetY, 2)) < 24.0d);
                            if (!suppress)
                            {
                                cands.Add(c);
                                if (cands.Count == k) break;
                            }
                        }
                    }
                    else
                    {
                        cands = rawCands.Take(k).ToList();
                    }
                    sw.Stop();
                    latencies.Add(sw.Elapsed.TotalMilliseconds);

                    if (cands.Count > 1)
                    {
                        var d = Math.Sqrt(Math.Pow(cands[0].OffsetX - cands[1].OffsetX, 2) + Math.Pow(cands[0].OffsetY - cands[1].OffsetY, 2));
                        dists.Add(d);
                    }

                    var hasGt = cands.Any(c =>
                        Math.Sqrt(Math.Pow(c.OffsetX - s.TrueOffsetX, 2) + Math.Pow(c.OffsetY - s.TrueOffsetY, 2)) <= 8.0d);
                    if (hasGt) gtHits++;
                }

                var gtRec = (double)gtHits / dataset.Count * 100.0;
                var avgDist = dists.Count > 0 ? dists.Average() : 0.0;
                var lat50 = Percentile(latencies, 0.50);

                sb.AppendLine($"| {name,-10} | {gtRec,8:F1}% | {avgDist,16:F1}px | {lat50,10:F2}ms |");
            }

            _output.WriteLine(sb.ToString());
            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                File.WriteAllText(Path.Combine(scratchDir, "phase3b_item4_topk.txt"), sb.ToString());
            }
            catch { }
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Diagnose_Phase3A_MarginTruth()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            var refDilK5 = new Dictionary<string, Mat>();
            var refDilK3 = new Dictionary<string, Mat>();
            foreach (var s in dataset)
            {
                if (!refDilK5.ContainsKey(s.ReferenceName))
                {
                    var d5 = new Mat(); var d3 = new Mat();
                    Cv2.Dilate(s.ReferenceStructureLine, d5, k5);
                    Cv2.Dilate(s.ReferenceStructureLine, d3, k3);
                    refDilK5[s.ReferenceName] = d5; refDilK3[s.ReferenceName] = d3;
                }
            }

            var sb = new StringBuilder();
            var hadDistinctRunnerUp = 0;
            var noDistinctRunnerUp = 0;

            foreach (var s in dataset)
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                    obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                if (peakRatio < 2.0d) continue;

                var estScale = sbRes.EstimatedScale;
                var rawCandidates = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                    obs.ObservedEdges, s.ReferenceStructureLine, s, estScale, topK: 50);
                if (rawCandidates.Count == 0) continue;

                var d5 = refDilK5[s.ReferenceName];
                var d3 = refDilK3[s.ReferenceName];

                var top1 = rawCandidates[0];
                (double OffsetX, double OffsetY, int Score)? runnerUp = null;
                foreach (var c in rawCandidates)
                {
                    var dist = Math.Sqrt(Math.Pow(c.OffsetX - top1.OffsetX, 2) + Math.Pow(c.OffsetY - top1.OffsetY, 2));
                    if (dist >= 24.0d)
                    {
                        runnerUp = c;
                        break;
                    }
                }

                // If no runner-up >= 24px, try >= 12px
                if (!runnerUp.HasValue)
                {
                    foreach (var c in rawCandidates)
                    {
                        var dist = Math.Sqrt(Math.Pow(c.OffsetX - top1.OffsetX, 2) + Math.Pow(c.OffsetY - top1.OffsetY, 2));
                        if (dist >= 12.0d)
                        {
                            runnerUp = c;
                            break;
                        }
                    }
                }

                var (rfS, rfX, rfY, rfScore) = Vpsg3Phase3ACorrectnessSuite.LocalRefineScaleAndTranslation(
                    obs.SparseEdgePoints, d5, d3, estScale, top1.OffsetX, top1.OffsetY,
                    s.ViewportBounds, obs.Width, obs.Height);
                var sp1 = Vpsg3Phase3ACorrectnessSuite.EvaluateSpatialVerification(
                    obs.SparseEdgePoints, d5, rfS, rfX, rfY, s.ViewportBounds, obs.Width, obs.Height);

                var secondK5Score = 0.0d;
                var foundDistinct = runnerUp.HasValue;
                if (runnerUp.HasValue)
                {
                    var sp2 = Vpsg3Phase3ACorrectnessSuite.EvaluateSpatialVerification(
                        obs.SparseEdgePoints, d5, estScale, runnerUp.Value.OffsetX, runnerUp.Value.OffsetY, s.ViewportBounds, obs.Width, obs.Height);
                    secondK5Score = sp2.GlobalScore;
                }

                if (foundDistinct) hadDistinctRunnerUp++;
                else noDistinctRunnerUp++;

                var margin = sp1.GlobalScore - secondK5Score;
                sb.AppendLine($"{s.Id,-15} Type={s.SourceType,-10} FoundDistinct={foundDistinct,-5} 1stScore={sp1.GlobalScore:F3} 2ndScore={secondK5Score:F3} Margin={margin:F3}");
            }

            sb.AppendLine($"\nTotal HadDistinct: {hadDistinctRunnerUp}, Total NoDistinct (Fake Margin): {noDistinctRunnerUp}");
            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                File.WriteAllText(Path.Combine(scratchDir, "phase3a_margin_diagnose.txt"), sb.ToString());
            } catch { }

            foreach (var kvp in refDilK5) kvp.Value.Dispose();
            foreach (var kvp in refDilK3) kvp.Value.Dispose();
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }
}
