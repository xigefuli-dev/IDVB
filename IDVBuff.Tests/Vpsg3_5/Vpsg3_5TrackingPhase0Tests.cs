using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3_5;

public sealed class Vpsg3_5TrackingPhase0Tests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3_5TrackingPhase0Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Vpsg3IndexCacheKey MakeKey(string refName) =>
        new(Guid.NewGuid(), "1F", "hash_" + refName, DateTimeOffset.UtcNow, "gen_" + refName);

    [Fact]
    public void Test1_LocalWindowTranslation_AccurateAndSubMillisecond()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var testedCount = 0;
            var latencies = new List<double>();
            var errors = new List<double>();

            foreach (var sample in dataset.Take(12))
            {
                using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
                var key = MakeKey(sample.ReferenceName);
                using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

                // Small prior shift (+/-8px)
                var priorTx = sample.TrueOffsetX + 6.0d;
                var priorTy = sample.TrueOffsetY - 4.0d;

                var (estTx, estTy, hits, score, success, lat) = Vpsg3_5TrackingPrototypes.EvaluateLocalWindowTranslation(
                    obs.SparseEdgePoints,
                    preparedFloor,
                    sample.TrueScale,
                    priorTx,
                    priorTy,
                    sample.ViewportBounds,
                    searchRadius: 36,
                    stride: 2,
                    minScoreThreshold: 0.35d);

                Assert.True(success, $"Local window translation should succeed on sample {sample.Id} with hits={hits}, score={score:F3}.");
                var err = Math.Sqrt(Math.Pow(estTx - sample.TrueOffsetX, 2) + Math.Pow(estTy - sample.TrueOffsetY, 2));
                Assert.True(err <= 3.5d, $"Localization error on sample {sample.Id} should be <= 3.5px, actual: {err:F2}px.");

                latencies.Add(lat);
                errors.Add(err);
                testedCount++;
            }

            latencies.Sort();
            var p50 = latencies[(int)(latencies.Count * 0.50)];
            var p95 = latencies[(int)(latencies.Count * 0.95)];
            var meanErr = errors.Average();

            _output.WriteLine($"Phase 0 Test 1: {testedCount} samples evaluated.");
            _output.WriteLine($"Latency P50: {p50:F3}ms, P95: {p95:F3}ms, Mean Error: {meanErr:F2}px.");

            Assert.True(p50 <= 2.0d, $"Expected P50 latency <= 2.0ms, actual: {p50:F3}ms");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test2_RadiusAndStrideTradeoff()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            using var obs = Vpsg3FastLiveExtractor.Extract(sample.LiveImage, sample.ViewportBounds);
            var key = MakeKey(sample.ReferenceName);
            using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            var radii = new[] { 24, 36, 48 };
            var strides = new[] { 2, 3 };

            foreach (var r in radii)
            {
                foreach (var s in strides)
                {
                    var priorTx = sample.TrueOffsetX + 10.0d;
                    var priorTy = sample.TrueOffsetY - 8.0d;

                    var (estTx, estTy, hits, score, success, lat) = Vpsg3_5TrackingPrototypes.EvaluateLocalWindowTranslation(
                        obs.SparseEdgePoints,
                        preparedFloor,
                        sample.TrueScale,
                        priorTx,
                        priorTy,
                        sample.ViewportBounds,
                        searchRadius: r,
                        stride: s,
                        minScoreThreshold: 0.35d);

                    var err = Math.Sqrt(Math.Pow(estTx - sample.TrueOffsetX, 2) + Math.Pow(estTy - sample.TrueOffsetY, 2));
                    _output.WriteLine($"Radius={r}px, Stride={s}px -> Latency={lat:F3}ms, Hits={hits}, Score={score:F3}, Error={err:F2}px");

                    Assert.True(success);
                    Assert.True(err <= 3.5d);
                }
            }
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void Test3_ConsecutiveTracking_100Frames_ZeroDriftVerified()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset[0];
            var key = MakeKey(sample.ReferenceName);
            using var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);

            var summary = Vpsg3_5TrackingPrototypes.RunContinuousTrackingSimulation(
                sample,
                preparedFloor,
                totalFrames: 100);

            _output.WriteLine($"Total Frames: {summary.TotalFrames}");
            _output.WriteLine($"Local Fast Path Count: {summary.LocalFastPathCount} / {summary.TotalFrames}");
            _output.WriteLine($"Fallback Count: {summary.FallbackCount}");
            _output.WriteLine($"Accepted Count: {summary.AcceptedCount} / {summary.TotalFrames}");
            _output.WriteLine($"Latency P50: {summary.P50LatencyMs:F3}ms, P95: {summary.P95LatencyMs:F3}ms, Max: {summary.MaxLatencyMs:F3}ms");
            _output.WriteLine($"Max Error: {summary.MaxErrorPixels:F2}px, Mean Error: {summary.MeanErrorPixels:F2}px");
            _output.WriteLine($"Zero Drift Verified: {summary.ZeroDriftVerified}");

            Assert.True(summary.LocalFastPathCount >= 95, $"Expected >= 95 frames on local fast path, actual: {summary.LocalFastPathCount}");
            Assert.True(summary.AcceptedCount >= 95, $"Expected >= 95 frames accepted, actual: {summary.AcceptedCount}");
            Assert.True(summary.ZeroDriftVerified, "Tracking must not accumulate drift error over consecutive frames.");
            Assert.True(summary.P50LatencyMs <= 2.0d, $"P50 Latency must be <= 2.0ms, actual: {summary.P50LatencyMs:F3}ms");
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }
}
