using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3Phase1;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class Vpsg3Phase1Benchmarks
{
    private readonly ITestOutputHelper _output;

    public Vpsg3Phase1Benchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Benchmark110FloorsMemoryFootprint()
    {
        _output.WriteLine("===============================================================================");
        _output.WriteLine("            VPSG 3.0 PHASE 1: 110 ELIGIBLE FLOORS MEMORY BENCHMARK             ");
        _output.WriteLine("===============================================================================");

        using var registry = new Vpsg3PreparedIndexRegistry();

        // Realistic floor dimensions observed across production IDVB maps:
        // Main floors: 1190x1012, 1200x1000, 1024x1024, 1280x960
        var dimensions = new (int W, int H)[]
        {
            (1190, 1012),
            (1200, 1000),
            (1024, 1024),
            (1280, 960),
            (960, 960)
        };

        var floorCount = 110;
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < floorCount; i++)
        {
            var (w, h) = dimensions[i % dimensions.Length];
            using var syntheticEdge = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));

            // Draw representative structural walls (horizontal and vertical lines)
            for (var y = 50; y < h - 50; y += 45)
                Cv2.Line(syntheticEdge, new Point(30, y), new Point(w - 30, y), Scalar.All(255), 2);
            for (var x = 50; x < w - 50; x += 55)
                Cv2.Line(syntheticEdge, new Point(x, 30), new Point(x, h - 30), Scalar.All(255), 2);

            var mapId = Guid.NewGuid();
            var floorKey = (i % 2 == 0) ? "1f" : "2f";
            var cacheKey = new Vpsg3IndexCacheKey(
                mapId,
                floorKey,
                $"fp-{i:D4}",
                DateTimeOffset.UtcNow,
                "gen1",
                SchemaVersion: 1);

            var prepared = Vpsg3PreparedIndexBuilder.BuildFromMat(syntheticEdge, cacheKey);
            registry.PublishFloor(prepared);
        }

        sw.Stop();

        var totalBytes = registry.TotalMemoryBytes;
        var totalMb = totalBytes / (1024.0 * 1024.0);
        var avgKb = (totalBytes / (double)floorCount) / 1024.0;

        _output.WriteLine($"[FLOORS REGISTERED] : {registry.Count}");
        _output.WriteLine($"[BUILD TIME TOTAL]  : {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"[TOTAL MEMORY]      : {totalMb:F2} MB ({totalBytes:N0} bytes)");
        _output.WriteLine($"[AVG PER FLOOR]     : {avgKb:F2} KB");
        _output.WriteLine("===============================================================================");

        Assert.Equal(110, registry.Count);
        // Total memory for 110 floors should be well under 30 MB (budget is < 35 MB)
        Assert.True(totalMb < 35.0, $"Expected total memory < 35 MB, but was {totalMb:F2} MB");
    }

    [Fact]
    public void BenchmarkWorkerScaling()
    {
        _output.WriteLine("===============================================================================");
        _output.WriteLine("          VPSG 3.0 PHASE 1: WORKER SCALING INITIALIZATION BENCHMARK            ");
        _output.WriteLine("===============================================================================");

        var floorCount = 110;
        var workerCounts = new[] { 1, 2, 4, 8 };

        // Pre-create 110 test edge mats to isolate initialization/building speed
        var mats = new List<(Mat EdgeMat, Vpsg3IndexCacheKey Key)>(floorCount);
        for (var i = 0; i < floorCount; i++)
        {
            var w = 1190;
            var h = 1012;
            var mat = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
            for (var y = 50; y < h - 50; y += 45)
                Cv2.Line(mat, new Point(30, y), new Point(w - 30, y), Scalar.All(255), 2);
            for (var x = 50; x < w - 50; x += 55)
                Cv2.Line(mat, new Point(x, 30), new Point(x, h - 30), Scalar.All(255), 2);

            var key = new Vpsg3IndexCacheKey(
                Guid.NewGuid(),
                (i % 2 == 0) ? "1f" : "2f",
                $"fp-{i:D4}",
                DateTimeOffset.UtcNow,
                "gen1",
                SchemaVersion: 1);

            mats.Add((mat, key));
        }

        var results = new List<(int Workers, double TotalMs, double Throughput, double Speedup)>();
        double baselineMs = 0d;

        try
        {
            foreach (var workers in workerCounts)
            {
                using var registry = new Vpsg3PreparedIndexRegistry();
                var sw = Stopwatch.StartNew();

                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = workers };
                Parallel.ForEach(mats, parallelOptions, item =>
                {
                    var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(item.EdgeMat, item.Key);
                    registry.PublishFloor(floor);
                });

                sw.Stop();
                var totalMs = sw.Elapsed.TotalMilliseconds;
                if (workers == 1)
                    baselineMs = totalMs;

                var throughput = (floorCount / (totalMs / 1000.0));
                var speedup = baselineMs / totalMs;
                results.Add((workers, totalMs, throughput, speedup));

                Assert.Equal(floorCount, registry.Count);
            }

            _output.WriteLine("| Workers | Total Time (ms) | Throughput (floors/s) | Speedup vs 1 Worker |");
            _output.WriteLine("|---------|-----------------|-----------------------|---------------------|");
            foreach (var r in results)
            {
                _output.WriteLine($"| {r.Workers,7} | {r.TotalMs,15:F2} | {r.Throughput,21:F1} | {r.Speedup,17:F2}x |");
            }
            _output.WriteLine("===============================================================================");
        }
        finally
        {
            foreach (var item in mats)
            {
                item.EdgeMat.Dispose();
            }
        }
    }
}
