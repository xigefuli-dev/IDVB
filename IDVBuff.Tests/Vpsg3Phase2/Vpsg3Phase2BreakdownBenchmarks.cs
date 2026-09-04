using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3Phase2BreakdownBenchmarks
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase2BreakdownBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class StageStats
    {
        public string Name { get; set; } = "";
        public List<double> LatenciesMs { get; } = new();
        public List<long> AllocationsBytes { get; } = new();

        public double P50 => Percentile(LatenciesMs, 0.50);
        public double P95 => Percentile(LatenciesMs, 0.95);
        public double P99 => Percentile(LatenciesMs, 0.99);
        public double Max => LatenciesMs.Count > 0 ? LatenciesMs.Max() : 0;
        public double MeanAlloc => AllocationsBytes.Count > 0 ? AllocationsBytes.Average() : 0;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        var idx = (int)Math.Floor(p * (sorted.Count - 1));
        return sorted[idx];
    }

    [Fact]
    public void Benchmark_StageLevelBreakdown_And_ResolutionAnalysis()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();

        try
        {
            // Warmup
            foreach (var s in dataset.Take(5))
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
            }

            var stageMap = new Dictionary<string, StageStats>
            {
                ["1. Color & Channel Normalize"] = new(),
                ["2. Dynamic Exclusion"] = new(),
                ["3. HSV & Pure Morphology"] = new(),
                ["4. Contour & ApproxPolyDP"] = new(),
                ["5. Canny Strong Support"] = new(),
                ["6. ObservedEdges (BitwiseAnd)"] = new(),
                ["7. Fog Frontier & ValidMask"] = new(),
                ["8. SparseEdgePoints Sampling"] = new(),
                ["9. Observation Construction"] = new(),
                ["10. Disposal"] = new()
            };

            var comboMap = new Dictionary<string, StageStats>
            {
                ["Edges-Only Core (1-6)"] = new(),
                ["Edges + ValidMask (1-7)"] = new(),
                ["Edges + ValidMask + SparsePoints (Full)"] = new()
            };

            // Stage-by-stage measurement on all samples
            foreach (var sample in dataset)
            {
                var img = sample.LiveImage;

                // 1. Color Normalize
                long allocStart = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                var bgr = EnsureBgr(img);
                var hsv = new Mat();
                Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
                sw.Stop();
                long allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["1. Color & Channel Normalize"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["1. Color & Channel Normalize"].AllocationsBytes.Add(allocEnd - allocStart);

                // 2. Dynamic Exclusion
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var exclusion = FastDynamicExclusion(bgr, hsv);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["2. Dynamic Exclusion"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["2. Dynamic Exclusion"].AllocationsBytes.Add(allocEnd - allocStart);

                // 3. HSV Classification & Morphology
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var (room, corridor) = ClassifyStructurePureMorphology(hsv, exclusion);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["3. HSV & Pure Morphology"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["3. HSV & Pure Morphology"].AllocationsBytes.Add(allocEnd - allocStart);

                // 4. Contour & ApproxPolyDP
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                using var roomEdges = SemanticCandidateEdges(room);
                using var corridorEdges = SemanticCandidateEdges(corridor);
                var candidateEdges = new Mat();
                Cv2.BitwiseOr(roomEdges, corridorEdges, candidateEdges);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["4. Contour & ApproxPolyDP"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["4. Contour & ApproxPolyDP"].AllocationsBytes.Add(allocEnd - allocStart);

                // 5. Canny Strong Support
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var support = StrongSourceEdgeSupport(bgr);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["5. Canny Strong Support"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["5. Canny Strong Support"].AllocationsBytes.Add(allocEnd - allocStart);

                // 6. ObservedEdges
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var observedEdges = new Mat();
                Cv2.BitwiseAnd(candidateEdges, support, observedEdges);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["6. ObservedEdges (BitwiseAnd)"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["6. ObservedEdges (BitwiseAnd)"].AllocationsBytes.Add(allocEnd - allocStart);

                // 7. Fog Frontier & ValidMask
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                using var notSupport = new Mat();
                Cv2.BitwiseNot(support, notSupport);
                using var uncertainFrontier = new Mat();
                Cv2.BitwiseAnd(candidateEdges, notSupport, uncertainFrontier);
                using var frontierKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 11));
                using var overlayKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
                using var dilatedFrontier = new Mat();
                Cv2.Dilate(uncertainFrontier, dilatedFrontier, frontierKernel);
                using var dilatedExclusion = new Mat();
                Cv2.Dilate(exclusion, dilatedExclusion, overlayKernel);
                using var invalid = new Mat();
                Cv2.BitwiseOr(dilatedFrontier, dilatedExclusion, invalid);
                var validMask = new Mat();
                Cv2.BitwiseNot(invalid, validMask);
                validMask.SetTo(Scalar.White, observedEdges);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["7. Fog Frontier & ValidMask"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["7. Fog Frontier & ValidMask"].AllocationsBytes.Add(allocEnd - allocStart);

                // 8. SparseEdgePoints Sampling
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var sparsePoints = SampleSparseEdgePoints(observedEdges, 150);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["8. SparseEdgePoints Sampling"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["8. SparseEdgePoints Sampling"].AllocationsBytes.Add(allocEnd - allocStart);

                // 9. Observation Construction
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var obs = new Vpsg3LiveObservation(
                    observedEdges: observedEdges,
                    validMask: validMask,
                    width: img.Width,
                    height: img.Height,
                    edgePixelCount: Cv2.CountNonZero(observedEdges),
                    validStructurePixelCount: Cv2.CountNonZero(validMask),
                    viewportBounds: sample.ViewportBounds,
                    sparseEdgePoints: sparsePoints,
                    extractionMilliseconds: 1.0);
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["9. Observation Construction"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["9. Observation Construction"].AllocationsBytes.Add(allocEnd - allocStart);

                // 10. Disposal
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                obs.Dispose();
                sw.Stop();
                allocEnd = GC.GetAllocatedBytesForCurrentThread();
                stageMap["10. Disposal"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                stageMap["10. Disposal"].AllocationsBytes.Add(allocEnd - allocStart);

                // Clean intermediate Mats
                bgr.Dispose();
                hsv.Dispose();
                exclusion.Dispose();
                room.Dispose();
                corridor.Dispose();
                candidateEdges.Dispose();
                support.Dispose();
            }

            // Measure Combos (Repeated 5 times per sample)
            foreach (var sample in dataset)
            {
                var img = sample.LiveImage;

                // Combo 1: Edges-Only Core
                for (var r = 0; r < 5; r++)
                {
                    var a0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    using var bgr = EnsureBgr(img);
                    using var hsv = new Mat();
                    Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
                    using var excl = FastDynamicExclusion(bgr, hsv);
                    var (room, corridor) = ClassifyStructurePureMorphology(hsv, excl);
                    using (room)
                    using (corridor)
                    {
                        using var re = SemanticCandidateEdges(room);
                        using var ce = SemanticCandidateEdges(corridor);
                        using var cands = new Mat();
                        Cv2.BitwiseOr(re, ce, cands);
                        using var supp = StrongSourceEdgeSupport(bgr);
                        using var obsEdges = new Mat();
                        Cv2.BitwiseAnd(cands, supp, obsEdges);
                        sw.Stop();
                        var a1 = GC.GetAllocatedBytesForCurrentThread();
                        comboMap["Edges-Only Core (1-6)"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                        comboMap["Edges-Only Core (1-6)"].AllocationsBytes.Add(a1 - a0);
                    }

                    // Combo 2: Edges + ValidMask
                    a0 = GC.GetAllocatedBytesForCurrentThread();
                    sw.Restart();
                    using var bgr2 = EnsureBgr(img);
                    using var hsv2 = new Mat();
                    Cv2.CvtColor(bgr2, hsv2, ColorConversionCodes.BGR2HSV);
                    using var excl2 = FastDynamicExclusion(bgr2, hsv2);
                    var (room2, corridor2) = ClassifyStructurePureMorphology(hsv2, excl2);
                    using (room2)
                    using (corridor2)
                    {
                        using var re = SemanticCandidateEdges(room2);
                        using var ce = SemanticCandidateEdges(corridor2);
                        using var cands = new Mat();
                        Cv2.BitwiseOr(re, ce, cands);
                        using var supp = StrongSourceEdgeSupport(bgr2);
                        using var obsEdges = new Mat();
                        Cv2.BitwiseAnd(cands, supp, obsEdges);

                        using var notSupp = new Mat();
                        Cv2.BitwiseNot(supp, notSupp);
                        using var unc = new Mat();
                        Cv2.BitwiseAnd(cands, notSupp, unc);
                        using var fk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 11));
                        using var ok = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
                        using var df = new Mat();
                        Cv2.Dilate(unc, df, fk);
                        using var de = new Mat();
                        Cv2.Dilate(excl2, de, ok);
                        using var inv = new Mat();
                        Cv2.BitwiseOr(df, de, inv);
                        using var vm = new Mat();
                        Cv2.BitwiseNot(inv, vm);
                        vm.SetTo(Scalar.White, obsEdges);
                        sw.Stop();
                        var a1Mask = GC.GetAllocatedBytesForCurrentThread();
                        comboMap["Edges + ValidMask (1-7)"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                        comboMap["Edges + ValidMask (1-7)"].AllocationsBytes.Add(a1Mask - a0);
                    }

                    // Combo 3: Full FastExtractor
                    a0 = GC.GetAllocatedBytesForCurrentThread();
                    sw.Restart();
                    using var fullObs = Vpsg3FastLiveExtractor.Extract(img, sample.ViewportBounds);
                    sw.Stop();
                    var a1Full = GC.GetAllocatedBytesForCurrentThread();
                    comboMap["Edges + ValidMask + SparsePoints (Full)"].LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                    comboMap["Edges + ValidMask + SparsePoints (Full)"].AllocationsBytes.Add(a1Full - a0);
                }
            }

            // Print Results
            var sb = new StringBuilder();
            sb.AppendLine("\n===============================================================================");
            sb.AppendLine("              VPSG 3.0 PHASE 2.1 STAGE BREAKDOWN & BOTTLENECK PROFILE           ");
            sb.AppendLine("===============================================================================");
            sb.AppendLine("| Pipeline Stage | P50 (ms) | P95 (ms) | P99 (ms) | Max (ms) | Mean Alloc (Bytes) |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: |");
            foreach (var kvp in stageMap)
            {
                var s = kvp.Value;
                sb.AppendLine($"| {kvp.Key,-30} | {s.P50,8:F2} | {s.P95,8:F2} | {s.P99,8:F2} | {s.Max,8:F2} | {s.MeanAlloc,18:N0} B |");
            }

            sb.AppendLine("\n-------------------------------------------------------------------------------");
            sb.AppendLine("                      COMBO & PIPELINE ACCUMULATION MATRIX                     ");
            sb.AppendLine("-------------------------------------------------------------------------------");
            sb.AppendLine("| Pipeline Configuration | P50 (ms) | P95 (ms) | P99 (ms) | Max (ms) | Mean Alloc (KB) |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: |");
            foreach (var kvp in comboMap)
            {
                var s = kvp.Value;
                sb.AppendLine($"| {kvp.Key,-40} | {s.P50,8:F2} | {s.P95,8:F2} | {s.P99,8:F2} | {s.Max,8:F2} | {s.MeanAlloc / 1024.0,14:F1} KB |");
            }

            // Resolution Breakdown
            sb.AppendLine("\n-------------------------------------------------------------------------------");
            sb.AppendLine("                       RESOLUTION-TIER LATENCY BREAKDOWN                       ");
            sb.AppendLine("-------------------------------------------------------------------------------");
            sb.AppendLine("| Resolution Group | Sample Count | Width x Height | P50 (ms) | P95 (ms) | P99 (ms) | Max (ms) |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

            var resGroups = dataset.GroupBy(s => $"{s.LiveImage.Width}x{s.LiveImage.Height} ({s.SourceType})").OrderBy(g => g.Key);
            foreach (var g in resGroups)
            {
                var lats = new List<double>();
                foreach (var sample in g)
                {
                    for (var r = 0; r < 5; r++)
                    {
                        var sw = Stopwatch.StartNew();
                        using var o = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                        sw.Stop();
                        lats.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }
                var p50 = Percentile(lats, 0.50);
                var p95 = Percentile(lats, 0.95);
                var p99 = Percentile(lats, 0.99);
                var max = lats.Max();
                var first = g.First();
                sb.AppendLine($"| {g.Key,-20} | {g.Count(),12} | {first.LiveImage.Width,5}x{first.LiveImage.Height,-5} | {p50,8:F2} | {p95,8:F2} | {p99,8:F2} | {max,8:F2} |");
            }

            _output.WriteLine(sb.ToString());
        }
        finally
        {
            foreach (var s in dataset)
                s.Dispose();
        }
    }

    #region Mirror Internal Methods for Step Isolation
    private static Mat EnsureBgr(Mat source)
    {
        var bgr = new Mat();
        switch (source.Channels())
        {
            case 4: Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR); break;
            case 3: source.CopyTo(bgr); break;
            case 1: Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR); break;
            default: throw new InvalidDataException();
        }
        return bgr;
    }

    private static Mat FastDynamicExclusion(Mat bgr, Mat hsv)
    {
        var h = bgr.Height;
        var w = bgr.Width;
        var exclusion = Mat.Zeros(bgr.Size(), MatType.CV_8UC1).ToMat();
        var greenRoiY = (int)(0.68 * h);
        var greenRoiW = (int)(0.28 * w);
        var greenRoiH = h - greenRoiY;
        if (greenRoiW > 0 && greenRoiH > 0)
        {
            var greenRect = new Rect(0, greenRoiY, greenRoiW, greenRoiH);
            using var hsvGreen = new Mat(hsv, greenRect);
            using var greenSeed = new Mat();
            Cv2.InRange(hsvGreen, new Scalar(35, 55, 45), new Scalar(95, 255, 255), greenSeed);
            if (Cv2.CountNonZero(greenSeed) > 40)
            {
                var fillRect = new Rect(0, (int)(0.72 * h), (int)(0.24 * w), (int)(0.28 * h));
                exclusion[fillRect].SetTo(Scalar.White);
            }
        }
        var topRoiY = (int)(0.03 * h);
        var topRoiH = (int)(0.12 * h);
        var topRoiX = (int)(0.10 * w);
        var topRoiW = (int)(0.70 * w);
        if (topRoiW > 0 && topRoiH > 0)
        {
            var topRect = new Rect(topRoiX, topRoiY, topRoiW, topRoiH);
            using var hsvTop = new Mat(hsv, topRect);
            using var whiteSeed = new Mat();
            Cv2.InRange(hsvTop, new Scalar(0, 0, 120), new Scalar(180, 60, 255), whiteSeed);
            if (Cv2.CountNonZero(whiteSeed) > 100)
            {
                exclusion[topRect].SetTo(Scalar.White);
            }
        }
        return exclusion;
    }

    private static (Mat Room, Mat Corridor) ClassifyStructurePureMorphology(Mat hsv, Mat exclusion)
    {
        var room = new Mat();
        using var room1 = new Mat();
        using var room2 = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 18, 82), new Scalar(25, 165, 200), room1);
        Cv2.InRange(hsv, new Scalar(170, 18, 82), new Scalar(179, 165, 200), room2);
        Cv2.BitwiseOr(room1, room2, room);
        var corridor = new Mat();
        Cv2.InRange(hsv, new Scalar(95, 14, 82), new Scalar(130, 105, 200), corridor);
        room.SetTo(Scalar.Black, exclusion);
        corridor.SetTo(Scalar.Black, exclusion);
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(room, room, MorphTypes.Open, k5);
        Cv2.MorphologyEx(room, room, MorphTypes.Close, k3);
        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Open, k5);
        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Close, k3);
        return (room, corridor);
    }

    private static Mat SemanticCandidateEdges(Mat mask)
    {
        var output = Mat.Zeros(mask.Size(), MatType.CV_8UC1).ToMat();
        Cv2.FindContours(mask, out var contours, out var hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0 || hierarchy.Length == 0) return output;
        for (var idx = 0; idx < contours.Length; idx++)
        {
            var contour = contours[idx];
            if (Cv2.ArcLength(contour, closed: true) < 30d) continue;
            var parent = hierarchy[idx].Parent;
            if (parent != -1 && Math.Abs(Cv2.ContourArea(contour)) < 900d) continue;
            var approx = Cv2.ApproxPolyDP(contour, 0.55d, closed: true);
            Cv2.DrawContours(output, [approx], -1, Scalar.White, 2, LineTypes.Link8);
        }
        return output;
    }

    private static Mat StrongSourceEdgeSupport(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var strong = new Mat();
        Cv2.Canny(gray, strong, 80d, 180d, apertureSize: 3, L2gradient: true);
        var support = new Mat();
        using var k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(strong, support, k5);
        return support;
    }

    private static Point[] SampleSparseEdgePoints(Mat edges, int maxPts)
    {
        if (maxPts <= 0) return [];
        var points = new List<Point>(Math.Min(maxPts * 4, 1024));
        var width = edges.Width;
        var height = edges.Height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (edges.At<byte>(y, x) > 128)
                    points.Add(new Point(x, y));
            }
        }
        if (points.Count <= maxPts) return points.ToArray();
        var result = new Point[maxPts];
        var step = (double)points.Count / maxPts;
        for (var i = 0; i < maxPts; i++)
            result[i] = points[(int)(i * step)];
        return result;
    }
    #endregion
}
