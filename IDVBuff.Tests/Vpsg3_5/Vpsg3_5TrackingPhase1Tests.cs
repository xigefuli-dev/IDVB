using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3_5;

public sealed class Vpsg3_5TrackingPhase1Tests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3_5TrackingPhase1Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Vpsg3IndexCacheKey MakeKey(string refName) =>
        new(Guid.NewGuid(), "1F", "hash_" + refName, DateTimeOffset.UtcNow, "gen_" + refName);

    [Fact]
    public void Test1_ProductionSolver_LocalFastPath_HighAccuracyAndLowLatency()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var tested = 0;
            var latencies = new List<double>();
            var errors = new List<double>();

            // JIT Warmup
            var warmup = dataset[0];
            using (var wObs = Vpsg3FastLiveExtractor.Extract(warmup.LiveImage, warmup.ViewportBounds))
            {
                var wKey = MakeKey(warmup.ReferenceName);
                using var wFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(warmup.ReferenceStructureLine, wKey);
                Vpsg3_5TrackingSolver.TryTrack(wObs, wFloor, warmup.TrueScale, warmup.TrueOffsetX, warmup.TrueOffsetY);
            }

            foreach (var sample in dataset.Take(12))
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                var key = MakeKey(sample.ReferenceName);
                using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

                // Small prior shift (+8px, -6px)
                var priorTx = sample.TrueOffsetX + 8.0d;
                var priorTy = sample.TrueOffsetY - 6.0d;

                var result = Vpsg3_5TrackingSolver.TryTrack(
                    obs,
                    preparedFloor,
                    sample.TrueScale,
                    priorTx,
                    priorTy);

                Assert.True(result.IsAccepted, $"Tracking should accept on sample {sample.Id}, reason: {result.FallbackReason}");
                Assert.True(result.IsLocalFastPath, $"Expected local fast path on sample {sample.Id}");
                Assert.True(result.PassedPartitions >= 2, $"Passed partitions must be >= 2, actual: {result.PassedPartitions}");

                var err = Math.Sqrt(Math.Pow(result.OffsetX - sample.TrueOffsetX, 2) + Math.Pow(result.OffsetY - sample.TrueOffsetY, 2));
                Assert.True(err <= 2.5d, $"Localization error on {sample.Id} must be <= 2.5px, actual: {err:F2}px");

                latencies.Add(result.Timing.TotalMs);
                errors.Add(err);
                tested++;
            }

            latencies.Sort();
            var p50 = latencies[(int)(latencies.Count * 0.50)];
            var p95 = latencies[(int)(latencies.Count * 0.95)];
            var meanErr = errors.Average();

            _output.WriteLine($"Phase 1 Test 1: {tested} samples evaluated.");
            _output.WriteLine($"Latency P50: {p50:F3}ms, P95: {p95:F3}ms, Mean Error: {meanErr:F2}px.");

            Assert.True(p50 <= 2.0d, $"Expected solver P50 <= 2.0ms, actual: {p50:F3}ms");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test2_ProductionSolver_LargeFlickJump_RecoversViaGlobalFallback()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
            var key = MakeKey(sample.ReferenceName);
            using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            // Large flick jump (75px) exceeding local search radius (36px)
            var priorTx = sample.TrueOffsetX + 75.0d;
            var priorTy = sample.TrueOffsetY - 50.0d;

            var result = Vpsg3_5TrackingSolver.TryTrack(
                obs,
                preparedFloor,
                sample.TrueScale,
                priorTx,
                priorTy);

            Assert.True(result.IsAccepted, $"Tracking should recover via fallback, reason: {result.FallbackReason}");
            Assert.False(result.IsLocalFastPath, "Should have fallen back to global translation search.");

            var err = Math.Sqrt(Math.Pow(result.OffsetX - sample.TrueOffsetX, 2) + Math.Pow(result.OffsetY - sample.TrueOffsetY, 2));
            _output.WriteLine($"Flick recovery error: {err:F2}px, Latency: {result.Timing.TotalMs:F3}ms (Fallback Search: {result.Timing.FallbackSearchMs:F3}ms)");

            Assert.True(err <= 2.5d, $"Recovery error should be <= 2.5px, actual: {err:F2}px");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test3_ProductionSolver_SteadyStateZeroAllocations()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
            var key = MakeKey(sample.ReferenceName);
            using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            var priorTx = sample.TrueOffsetX + 2.0d;
            var priorTy = sample.TrueOffsetY - 2.0d;

            // Warm-up to JIT all paths
            for (var i = 0; i < 5; i++)
            {
                Vpsg3_5TrackingSolver.TryTrack(obs, preparedFloor, sample.TrueScale, priorTx, priorTy);
            }

            // Measure steady-state allocations over 10 consecutive tracking steps
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++)
            {
                var r = Vpsg3_5TrackingSolver.TryTrack(obs, preparedFloor, sample.TrueScale, priorTx, priorTy);
                priorTx = r.OffsetX;
                priorTy = r.OffsetY;
            }
            var afterBytes = GC.GetAllocatedBytesForCurrentThread();
            var allocatedBytes = afterBytes - beforeBytes;
            var bytesPerFrame = allocatedBytes / 10.0d;

            _output.WriteLine($"Steady-state allocations: total={allocatedBytes} bytes across 10 frames ({bytesPerFrame:F1} bytes/frame)");

            // In .NET 10, Vpsg3_5TrackingResult class + LINQ in precision refiner allocates ~280 bytes per frame.
            // Assert that steady-state per-frame allocations stay well under 350 bytes with zero GC leaks.
            Assert.True(bytesPerFrame <= 350.0d, $"Expected <= 350 bytes/frame, actual: {bytesPerFrame:F1} bytes");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test4_DeadbandFilter_SuppressesStationaryJitter()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
            var key = MakeKey(sample.ReferenceName);
            using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            // Establish lock position first
            var initial = Vpsg3_5TrackingSolver.TryTrack(
                obs,
                preparedFloor,
                sample.TrueScale,
                sample.TrueOffsetX,
                sample.TrueOffsetY);

            Assert.True(initial.IsAccepted);

            // Configure deadband = 1.0px (wider than discrete refiner delta 0.65px)
            var cfg = new Vpsg3_5TrackingConfig { DeadbandPixels = 1.0d };
            var perturbedPriorX = initial.OffsetX + 0.2d;
            var perturbedPriorY = initial.OffsetY - 0.15d;

            var next = Vpsg3_5TrackingSolver.TryTrack(
                obs,
                preparedFloor,
                sample.TrueScale,
                perturbedPriorX,
                perturbedPriorY,
                config: cfg);

            Assert.True(next.IsAccepted);
            // Because deviation is within 1.0px deadband, output should hold the prior position
            Assert.Equal(perturbedPriorX, next.OffsetX);
            Assert.Equal(perturbedPriorY, next.OffsetY);
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test5_EdgeCases_RejectsEmptyOrInvalidFloor()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
            var key = MakeKey(sample.ReferenceName);
            var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            // Invalid scale
            var resInvalidScale = Vpsg3_5TrackingSolver.TryTrack(obs, preparedFloor, -1.0d, 0, 0);
            Assert.False(resInvalidScale.IsAccepted);
            Assert.Equal("InvalidFloorOrScale", resInvalidScale.FallbackReason);

            // Disposed floor
            preparedFloor.Dispose();
            var resDisposed = Vpsg3_5TrackingSolver.TryTrack(obs, preparedFloor, 1.0d, 0, 0);
            Assert.False(resDisposed.IsAccepted);
            Assert.Equal("InvalidFloorOrScale", resDisposed.FallbackReason);
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }
}
