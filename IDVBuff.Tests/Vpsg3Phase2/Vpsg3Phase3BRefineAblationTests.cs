using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3Phase3BRefineAblationTests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase3BRefineAblationTests(ITestOutputHelper output)
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
    public void Benchmark_Item7_RefinementProbeReduction()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            var refDilK5 = new Dictionary<string, Mat>();
            var refDilK3 = new Dictionary<string, Mat>();

            foreach (var s in dataset)
            {
                if (!refDilK5.ContainsKey(s.ReferenceName))
                {
                    var d5 = new Mat();
                    var d3 = new Mat();
                    Cv2.Dilate(s.ReferenceStructureLine, d5, k5);
                    Cv2.Dilate(s.ReferenceStructureLine, d3, k3);
                    refDilK5[s.ReferenceName] = d5;
                    refDilK3[s.ReferenceName] = d3;
                }
            }

            var strategies = new[] { "StrategyA_441", "StrategyB_CoarseFine", "StrategyC_Separable", "StrategyD_CoordDescent", "StrategyE_ScaleThenTrans", "StrategyF_Iterative3x3" };
            var sb = new StringBuilder();
            sb.AppendLine("=================================================================================================================================");
            sb.AppendLine("                       ITEM 7: REFINEMENT PROBE REDUCTION ABLATION (57 SAMPLES)                                  ");
            sb.AppendLine("=================================================================================================================================");
            sb.AppendLine("| Strategy | Description | Avg Probes | WrongAccept | FastCov | Trans P50 | Trans P95 | Scale P50 | Scale P95 | Latency P50 |");
            sb.AppendLine("| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var strat in strategies)
            {
                var probeCounts = new List<int>();
                var transErrors = new List<double>();
                var scaleErrors = new List<double>();
                var latencies = new List<double>();
                var fastAccepted = 0;
                var wrongAccepted = 0;

                foreach (var s in dataset)
                {
                    using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                    var (sbRes, peakRatio) = Vpsg3ScalePrototypes.EvaluateScaleMethodB(
                        obs.ObservedEdges, s.ReferenceStructureLine, s.TrueScale, s.Id, s.SourceType);
                    if (peakRatio < 2.0d) continue;

                    var estScale = sbRes.EstimatedScale;
                    var cands = Vpsg3TranslationPrototypes.GenerateT3Candidates(
                        obs.ObservedEdges, s.ReferenceStructureLine, s, estScale, topK: 50);
                    if (cands.Count == 0) continue;

                    // Top-2 with dist > 6.0px (exact Phase 3A criterion)
                    var top1 = cands[0];
                    (double OffsetX, double OffsetY, int Score)? top2 = null;
                    foreach (var c in cands)
                    {
                        var dist = Math.Sqrt(Math.Pow(c.OffsetX - top1.OffsetX, 2) + Math.Pow(c.OffsetY - top1.OffsetY, 2));
                        if (dist > 6.0d)
                        {
                            top2 = c;
                            break;
                        }
                    }

                    var d5 = refDilK5[s.ReferenceName];
                    var d3 = refDilK3[s.ReferenceName];

                    var sw = Stopwatch.StartNew();
                    var (rScale, rX, rY, rScore, probes) = RunRefinementStrategy(
                        strat, obs.SparseEdgePoints, d5, d3, estScale, top1.OffsetX, top1.OffsetY,
                        s.ViewportBounds, obs.Width, obs.Height);
                    sw.Stop();

                    probeCounts.Add(probes);
                    latencies.Add(sw.Elapsed.TotalMilliseconds);

                    var sp = Vpsg3Phase3ACorrectnessSuite.EvaluateSpatialVerification(
                        obs.SparseEdgePoints, d5, rScale, rX, rY, s.ViewportBounds, obs.Width, obs.Height);

                    double cand2Score = 0.0d;
                    if (top2.HasValue)
                    {
                        var (rS2, rX2, rY2, _, p2) = RunRefinementStrategy(
                            strat, obs.SparseEdgePoints, d5, d3, estScale, top2.Value.OffsetX, top2.Value.OffsetY,
                            s.ViewportBounds, obs.Width, obs.Height);
                        probeCounts[probeCounts.Count - 1] += p2;
                        var sp2 = Vpsg3Phase3ACorrectnessSuite.EvaluateSpatialVerification(
                            obs.SparseEdgePoints, d5, rS2, rX2, rY2, s.ViewportBounds, obs.Width, obs.Height);
                        cand2Score = sp2.GlobalScore;
                    }

                    var k5Margin = top2.HasValue ? sp.GlobalScore - cand2Score : 0.0d;
                    var passGate = rScore >= 0.60d && sp.PassedPartitions >= 3 && top2.HasValue && k5Margin >= 0.09d;
                    if (passGate)
                    {
                        fastAccepted++;
                        var sErr = Math.Abs(rScale - s.TrueScale);
                        var tErr = Math.Sqrt(Math.Pow(rX - s.TrueOffsetX, 2) + Math.Pow(rY - s.TrueOffsetY, 2));
                        scaleErrors.Add(sErr);
                        transErrors.Add(tErr);

                        if (sErr > 0.035d || tErr > 4.0d)
                        {
                            wrongAccepted++;
                            sb.AppendLine($"[{strat} WRONG] {s.Id}: sErr={sErr:F4}, tErr={tErr:F2}px, score={rScore:F3}, margin={k5Margin:F3}, 2ndScore={cand2Score:F3}");
                        }
                    }
                }

                var avgP = probeCounts.Average();
                var cov = (double)fastAccepted / dataset.Count * 100.0;
                var t50 = Percentile(transErrors, 0.50);
                var t95 = Percentile(transErrors, 0.95);
                var s50 = Percentile(scaleErrors, 0.50);
                var s95 = Percentile(scaleErrors, 0.95);
                var lat50 = Percentile(latencies, 0.50);

                sb.AppendLine($"| {strat,-22} | {GetStrategyDesc(strat),-11} | {avgP,10:F1} | {wrongAccepted,11} | {cov,6:F1}% | {t50,7:F2}px | {t95,7:F2}px | {s50,7:F4} | {s95,7:F4} | {lat50,9:F2}ms |");
            }

            _output.WriteLine(sb.ToString());
            try
            {
                var scratchDir = Path.Combine(AppContext.BaseDirectory, "../../../../scratch");
                if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                File.WriteAllText(Path.Combine(scratchDir, "phase3b_item7_refinement.txt"), sb.ToString());
            }
            catch { }
            foreach (var kvp in refDilK5) kvp.Value.Dispose();
            foreach (var kvp in refDilK3) kvp.Value.Dispose();
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    private static string GetStrategyDesc(string s) => s switch
    {
        "StrategyA_441" => "Brute 9x7x7",
        "StrategyB_CoarseFine" => "Coarse-Fine",
        "StrategyC_Separable" => "Trans->Scale",
        "StrategyD_CoordDescent" => "CoordDescent",
        "StrategyE_ScaleThenTrans" => "Scale->Trans",
        "StrategyF_Iterative3x3" => "3x3 Polish",
        _ => s
    };

    private static (double Scale, double X, double Y, double Score, int Probes) RunRefinementStrategy(
        string strategy,
        IReadOnlyList<Point> sparsePoints,
        Mat refDilK5,
        Mat refDilK3,
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
        var refW = refDilK5.Width;
        var refH = refDilK5.Height;

        double Eval(double s, double x, double y)
        {
            var h5 = 0;
            var h3 = 0;
            foreach (var q in sparsePoints)
            {
                var sx = viewportBounds.X + q.X;
                var sy = viewportBounds.Y + q.Y;
                var rx = (int)Math.Round((sx - x) / s);
                var ry = (int)Math.Round((sy - y) / s);
                if (rx >= 0 && rx < refW && ry >= 0 && ry < refH)
                {
                    if (refDilK5.At<byte>(ry, rx) > 128)
                    {
                        h5++;
                        if (refDilK3.At<byte>(ry, rx) > 128) h3++;
                    }
                }
            }
            return (h5 + 2.0d * h3) / (3.0d * Math.Max(1, sparsePoints.Count));
        }

        var probes = 0;
        if (strategy == "StrategyA_441")
        {
            var sDeltas = new[] { 0.000d, -0.005d, 0.005d, -0.010d, 0.010d, -0.015d, 0.015d, -0.020d, 0.020d };
            var tDeltas = new[] { 0.0d, -2.0d, 2.0d, -4.0d, 4.0d, -6.0d, 6.0d };
            var bestScore = -1.0d;
            var (bS, bX, bY) = (seedScale, seedX, seedY);
            foreach (var ds in sDeltas)
            {
                var cs = seedScale + ds;
                var bx = cx - rcx * cs;
                var by = cy - rcy * cs;
                foreach (var dx in tDeltas)
                {
                    foreach (var dy in tDeltas)
                    {
                        probes++;
                        var sc = Eval(cs, bx + dx, by + dy);
                        if (sc > bestScore) { bestScore = sc; bS = cs; bX = bx + dx; bY = by + dy; }
                    }
                }
            }
            return (bS, bX, bY, bestScore, probes);
        }
        else if (strategy == "StrategyB_CoarseFine")
        {
            var sCoarse = new[] { -0.015d, 0.000d, 0.015d };
            var tCoarse = new[] { -4.0d, 0.0d, 4.0d };
            var bestScore = -1.0d;
            var (bS, bX, bY) = (seedScale, seedX, seedY);
            var (bestDs, bestDx, bestDy) = (0.0d, 0.0d, 0.0d);

            foreach (var ds in sCoarse)
            {
                var cs = seedScale + ds;
                var bx = cx - rcx * cs;
                var by = cy - rcy * cs;
                foreach (var dx in tCoarse)
                {
                    foreach (var dy in tCoarse)
                    {
                        probes++;
                        var sc = Eval(cs, bx + dx, by + dy);
                        if (sc > bestScore) { bestScore = sc; bS = cs; bX = bx + dx; bY = by + dy; bestDs = ds; bestDx = dx; bestDy = dy; }
                    }
                }
            }

            var sFine = new[] { -0.005d, 0.000d, 0.005d };
            var tFine = new[] { -2.0d, 0.0d, 2.0d };
            foreach (var fds in sFine)
            {
                var cs = seedScale + bestDs + fds;
                var bx = cx - rcx * cs;
                var by = cy - rcy * cs;
                foreach (var fdx in tFine)
                {
                    foreach (var fdy in tFine)
                    {
                        if (fds == 0 && fdx == 0 && fdy == 0) continue;
                        probes++;
                        var curX = bx + bestDx + fdx;
                        var curY = by + bestDy + fdy;
                        var sc = Eval(cs, curX, curY);
                        if (sc > bestScore) { bestScore = sc; bS = cs; bX = curX; bY = curY; }
                    }
                }
            }
            return (bS, bX, bY, bestScore, probes);
        }
        else if (strategy == "StrategyC_Separable" || strategy == "StrategyF_Iterative3x3")
        {
            var tDeltas = new[] { -4.0d, -2.0d, 0.0d, 2.0d, 4.0d };
            var bestScore = -1.0d;
            var (bX, bY) = (seedX, seedY);
            foreach (var dx in tDeltas)
            {
                foreach (var dy in tDeltas)
                {
                    probes++;
                    var sc = Eval(seedScale, seedX + dx, seedY + dy);
                    if (sc > bestScore) { bestScore = sc; bX = seedX + dx; bY = seedY + dy; }
                }
            }

            var rCentX = (cx - bX) / seedScale;
            var rCentY = (cy - bY) / seedScale;
            var sDeltas = new[] { -0.015d, -0.010d, -0.005d, 0.000d, 0.005d, 0.010d, 0.015d };
            var bS = seedScale;
            var bX2 = bX;
            var bY2 = bY;

            foreach (var ds in sDeltas)
            {
                if (ds == 0.0d) continue;
                var cs = seedScale + ds;
                var nx = cx - rCentX * cs;
                var ny = cy - rCentY * cs;
                probes++;
                var sc = Eval(cs, nx, ny);
                if (sc > bestScore) { bestScore = sc; bS = cs; bX2 = nx; bY2 = ny; }
            }

            var fineT = new[] { -1.5d, 0.0d, 1.5d };
            var fineS = new[] { -0.005d, 0.0d, 0.005d };
            var finalX = bX2;
            var finalY = bY2;
            var finalS = bS;
            var rCentX2 = (cx - bX2) / bS;
            var rCentY2 = (cy - bY2) / bS;

            foreach (var fds in fineS)
            {
                var cs = bS + fds;
                var nx = cx - rCentX2 * cs;
                var ny = cy - rCentY2 * cs;
                foreach (var fdx in fineT)
                {
                    foreach (var fdy in fineT)
                    {
                        if (fds == 0.0d && fdx == 0.0d && fdy == 0.0d) continue;
                        probes++;
                        var sc = Eval(cs, nx + fdx, ny + fdy);
                        if (sc > bestScore) { bestScore = sc; finalS = cs; finalX = nx + fdx; finalY = ny + fdy; }
                    }
                }
            }

            return (finalS, finalX, finalY, bestScore, probes);
        }
        else if (strategy == "StrategyE_ScaleThenTrans")
        {
            var sDeltas = new[] { -0.020d, -0.015d, -0.010d, -0.005d, 0.000d, 0.005d, 0.010d, 0.015d, 0.020d };
            var bestScore = -1.0d;
            var bS = seedScale;
            var bX = seedX;
            var bY = seedY;

            foreach (var ds in sDeltas)
            {
                var cs = seedScale + ds;
                var nx = cx - rcx * cs;
                var ny = cy - rcy * cs;
                probes++;
                var sc = Eval(cs, nx, ny);
                if (sc > bestScore) { bestScore = sc; bS = cs; bX = nx; bY = ny; }
            }

            var tDeltas = new[] { -4.0d, -2.0d, 0.0d, 2.0d, 4.0d };
            var bX2 = bX;
            var bY2 = bY;
            foreach (var dx in tDeltas)
            {
                foreach (var dy in tDeltas)
                {
                    if (dx == 0 && dy == 0) continue;
                    probes++;
                    var sc = Eval(bS, bX + dx, bY + dy);
                    if (sc > bestScore) { bestScore = sc; bX2 = bX + dx; bY2 = bY + dy; }
                }
            }

            var fineT = new[] { -1.0d, 0.0d, 1.0d };
            var finalX = bX2;
            var finalY = bY2;
            foreach (var fdx in fineT)
            {
                foreach (var fdy in fineT)
                {
                    if (fdx == 0.0d && fdy == 0.0d) continue;
                    probes++;
                    var sc = Eval(bS, bX2 + fdx, bY2 + fdy);
                    if (sc > bestScore) { bestScore = sc; finalX = bX2 + fdx; finalY = bY2 + fdy; }
                }
            }

            return (bS, finalX, finalY, bestScore, probes);
        }
        else // Coordinate Descent
        {
            var curS = seedScale;
            var curX = seedX;
            var curY = seedY;
            var bestScore = Eval(curS, curX, curY);
            probes++;

            for (var iter = 0; iter < 2; iter++)
            {
                var moved = false;
                foreach (var (dx, dy) in new[] { (-2.0, 0.0), (2.0, 0.0), (0.0, -2.0), (0.0, 2.0) })
                {
                    probes++;
                    var sc = Eval(curS, curX + dx, curY + dy);
                    if (sc > bestScore) { bestScore = sc; curX += dx; curY += dy; moved = true; break; }
                }
                if (!moved) break;
            }

            var rX = (cx - curX) / curS;
            var rY = (cy - curY) / curS;
            foreach (var ds in new[] { -0.010, 0.010, -0.005, 0.005 })
            {
                var ns = curS + ds;
                var nx = cx - rX * ns;
                var ny = cy - rY * ns;
                probes++;
                var sc = Eval(ns, nx, ny);
                if (sc > bestScore) { bestScore = sc; curS = ns; curX = nx; curY = ny; break; }
            }

            foreach (var (dx, dy) in new[] { (-1.0, 0.0), (1.0, 0.0), (0.0, -1.0), (0.0, 1.0) })
            {
                probes++;
                var sc = Eval(curS, curX + dx, curY + dy);
                if (sc > bestScore) { bestScore = sc; curX += dx; curY += dy; }
            }

            return (curS, curX, curY, bestScore, probes);
        }
    }
}
