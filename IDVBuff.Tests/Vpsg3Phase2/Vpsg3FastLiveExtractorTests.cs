using System.Diagnostics;
using System.Text;
using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3FastLiveExtractorTests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3FastLiveExtractorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DifferentialTest_MatchesA4PrototypePixelPerfect()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var mismatchSamples = 0;
            var evaluated = 0;

            foreach (var sample in dataset)
            {
                evaluated++;
                // 1. Run Phase 0 A-4 prototype
                using var a4Result = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(sample.LiveImage);

                // 2. Run Phase 2 production Vpsg3FastLiveExtractor
                using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);

                // 3. Pixel-level comparison of ObservedEdges
                using var diff = new Mat();
                Cv2.Absdiff(a4Result.Edges, obs.ObservedEdges, diff);
                var diffCount = Cv2.CountNonZero(diff);

                if (diffCount > 0)
                {
                    mismatchSamples++;
                }

                Assert.Equal(0, diffCount);
            }

            Assert.True(evaluated > 0);
            Assert.Equal(0, mismatchSamples);
        }
        finally
        {
            foreach (var s in dataset)
                s.Dispose();
        }
    }

    [Fact]
    public void CoordinateContract_And_ValidMaskSemantics_ArePreserved()
    {
        using var testMat = new Mat(400, 600, MatType.CV_8UC3, new Scalar(40, 40, 40));
        // Draw simulated room (hsv inside room gamut: e.g. BGR (80, 80, 160))
        Cv2.Rectangle(testMat, new Rect(50, 50, 200, 200), new Scalar(80, 80, 160), -1);
        Cv2.Rectangle(testMat, new Rect(50, 50, 200, 200), new Scalar(255, 255, 255), 2); // strong edge

        var bounds = new MapScreenRect(100, 200, 600, 400);
        using var obs = Vpsg3FastLiveExtractor.Extract(testMat, bounds);

        Assert.Equal(600, obs.Width);
        Assert.Equal(400, obs.Height);
        Assert.Equal(bounds, obs.ViewportBounds);
        Assert.Equal(Vpsg3CoordinateSpace.LocalViewport, obs.CoordinateSpace);
        Assert.False(obs.IsDisposed);
        Assert.True(obs.EdgePixelCount > 0);
        Assert.True(obs.ValidStructurePixelCount > 0);
        Assert.True(obs.SparseEdgePoints.Count > 0);

        // Verification points must be within [0, width) x [0, height)
        foreach (var pt in obs.SparseEdgePoints)
        {
            Assert.InRange(pt.X, 0, 599);
            Assert.InRange(pt.Y, 0, 399);
        }

        // Disposal cleans up underlying native mats
        obs.Dispose();
        Assert.True(obs.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => { _ = obs.ObservedEdges; });
        Assert.Throws<ObjectDisposedException>(() => { _ = obs.ValidMask; });
    }

    [Fact]
    public void PrebuiltReferenceSymmetry_ExtractsCleanEdgesFromSyntheticReference()
    {
        // When fed with clean reference floor image without fog or HUD,
        // FastLiveExtractor must extract structural edges that have high agreement with the reference line.
        var (synColor, synLine) = Vpsg3Phase0DatasetGenerator.BuildSyntheticReference(800, 600);
        try
        {
            using var obs = Vpsg3FastLiveExtractor.Extract(synColor);

            Assert.Equal(synLine.Width, obs.Width);
            Assert.Equal(synLine.Height, obs.Height);

            // Compute agreement with reference structure line
            using var candDilated = new Mat();
            using var refDilated = new Mat();
            using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

            Cv2.Dilate(obs.ObservedEdges, candDilated, k3);
            Cv2.Dilate(synLine, refDilated, k3);

            using var candMatch = new Mat();
            Cv2.BitwiseAnd(obs.ObservedEdges, refDilated, candMatch);
            var matchedPixels = Cv2.CountNonZero(candMatch);
            var totalObserved = Cv2.CountNonZero(obs.ObservedEdges);

            var agreement = totalObserved > 0 ? (double)matchedPixels / totalObserved : 0d;
            _output.WriteLine($"[Symmetry Test] Clean full floor observed edges: {totalObserved}, matched with ref: {matchedPixels}, agreement: {agreement:P2}");
            Assert.True(agreement >= 0.85d, $"Expected clean symmetry agreement >= 85%, got {agreement:P1}");
        }
        finally
        {
            synColor.Dispose();
            synLine.Dispose();
        }
    }

    [Fact]
    public void Benchmark_ExtractorComparison_OldVsA4VsPhase2()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var oldTimes = new List<double>();
            var a4Times = new List<double>();
            var p2Times = new List<double>();

            var oldEdgesList = new List<Mat>();
            var a4EdgesList = new List<Mat>();
            var p2ObsList = new List<Vpsg3LiveObservation>();

            var p2Allocations = new List<long>();

            foreach (var s in dataset)
            {
                // 1. Old production extractor
                var swOld = Stopwatch.StartNew();
                using var oldRes = IdvaNativeObservedExtractor.Process(s.LiveImage);
                swOld.Stop();
                oldTimes.Add(swOld.Elapsed.TotalMilliseconds);
                oldEdgesList.Add(oldRes.ObservedEdges.Clone());

                // 2. Phase 0 A-4 prototype
                var swA4 = Stopwatch.StartNew();
                var a4Step = Vpsg3FastIdvaPrototypes.RunA4CheapExclusion(s.LiveImage);
                swA4.Stop();
                a4Times.Add(swA4.Elapsed.TotalMilliseconds);
                a4EdgesList.Add(a4Step.Edges);

                // 3. Phase 2 Vpsg3FastLiveExtractor
                var memBefore = GC.GetAllocatedBytesForCurrentThread();
                var swP2 = Stopwatch.StartNew();
                var p2Obs = Vpsg3FastLiveExtractor.Extract(s.LiveImage, s.ViewportBounds);
                swP2.Stop();
                var memAfter = GC.GetAllocatedBytesForCurrentThread();
                p2Times.Add(swP2.Elapsed.TotalMilliseconds);
                p2Allocations.Add(memAfter - memBefore);
                p2ObsList.Add(p2Obs);
            }

            // Metric computations
            var sb = new StringBuilder();
            sb.AppendLine("\n--- [VPSG3 PHASE 2 LIVE EXTRACTOR BENCHMARK MATRIX] ---");
            sb.AppendLine("| Extractor | P50 (ms) | P95 (ms) | P99 (ms) | Max (ms) | Mean Alloc (KB) | Pixel Agreement vs A-4 | ValidMask IoU vs Old |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            oldTimes.Sort();
            a4Times.Sort();
            p2Times.Sort();

            var oldP50 = oldTimes[(int)(oldTimes.Count * 0.50)];
            var oldP95 = oldTimes[(int)(oldTimes.Count * 0.95)];
            var oldP99 = oldTimes[(int)(oldTimes.Count * 0.99)];
            var oldMax = oldTimes[^1];

            var a4P50 = a4Times[(int)(a4Times.Count * 0.50)];
            var a4P95 = a4Times[(int)(a4Times.Count * 0.95)];
            var a4P99 = a4Times[(int)(a4Times.Count * 0.99)];
            var a4Max = a4Times[^1];

            var p2P50 = p2Times[(int)(p2Times.Count * 0.50)];
            var p2P95 = p2Times[(int)(p2Times.Count * 0.95)];
            var p2P99 = p2Times[(int)(p2Times.Count * 0.99)];
            var p2Max = p2Times[^1];
            var meanAllocKb = p2Allocations.Average() / 1024.0;

            // Check IoU and pixel agreement across all samples
            var exactMatches = 0;
            var iouSum = 0d;

            for (var i = 0; i < dataset.Count; i++)
            {
                using var diff = new Mat();
                Cv2.Absdiff(a4EdgesList[i], p2ObsList[i].ObservedEdges, diff);
                if (Cv2.CountNonZero(diff) == 0) exactMatches++;

                // Compare ValidMask against Old IdvaNativeObservedExtractor ValidMask
                using var oldRes = IdvaNativeObservedExtractor.Process(dataset[i].LiveImage);
                using var intersection = new Mat();
                using var union = new Mat();
                Cv2.BitwiseAnd(oldRes.ValidMask, p2ObsList[i].ValidMask, intersection);
                Cv2.BitwiseOr(oldRes.ValidMask, p2ObsList[i].ValidMask, union);
                var u = Cv2.CountNonZero(union);
                var iou = u > 0 ? (double)Cv2.CountNonZero(intersection) / u : 1.0;
                iouSum += iou;
            }

            var agreementPct = (double)exactMatches / dataset.Count * 100.0;
            var avgIouPct = (iouSum / dataset.Count) * 100.0;

            sb.AppendLine($"| Old IdvaNativeObserved | {oldP50,8:F2} | {oldP95,8:F2} | {oldP99,8:F2} | {oldMax,8:F2} | {"N/A",15} | {"Baseline",23} | {"100.0%",21} |");
            sb.AppendLine($"| Phase 0 A-4 Prototype  | {a4P50,8:F2} | {a4P95,8:F2} | {a4P99,8:F2} | {a4Max,8:F2} | {"N/A",15} | {"100.0%",23} | {"N/A",21} |");
            sb.AppendLine($"| Phase 2 FastExtractor  | {p2P50,8:F2} | {p2P95,8:F2} | {p2P99,8:F2} | {p2Max,8:F2} | {meanAllocKb,12:F1} KB | {agreementPct,22:F1}% | {avgIouPct,20:F1}% |");

            _output.WriteLine(sb.ToString());

            foreach (var m in oldEdgesList) m.Dispose();
            foreach (var m in a4EdgesList) m.Dispose();
            foreach (var obs in p2ObsList) obs.Dispose();
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }
}
