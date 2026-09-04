using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests.Vpsg3Phase2;

public sealed class Vpsg3ShadowAndGateTests
{
    [Theory]
    [InlineData(1320, 1037)] // Actual viewport size from the 2026-09-04 live log.
    [InlineData(2560, 1080)]
    public void PhysicalViewportBeyondOneMegapixelHasReusableScaleScratch(int width, int height)
    {
        using var reference = new Mat(600, 800, MatType.CV_8UC1, Scalar.Black);
        for (var x = 20; x < 800; x += 40) Cv2.Line(reference, new(x, 0), new(x, 599), Scalar.White, 2);
        using var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(reference,
            new(Guid.NewGuid(), "1f", "test", DateTimeOffset.UnixEpoch, "test"));
        Assert.True(floor.ScalePrior.FastPathEligible);
        var edges = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        for (var x = 20; x < width; x += 40) Cv2.Line(edges, new(x, 0), new(x, height - 1), Scalar.White, 2);
        using var observation = new Vpsg3LiveObservation(edges, new Mat(height, width, MatType.CV_8UC1, Scalar.White),
            width, height, Cv2.CountNonZero(edges), width * height, new(100, 50, width, height));
        var scratch = new Vpsg3SolverScratch();
        var first = Vpsg3ScaleSolver.Solve(observation, floor, scratch: scratch);
        var buffer = scratch.EdgeMaskBuffer;
        Assert.True(buffer.Length >= width * height);
        Assert.Equal(first, Vpsg3ScaleSolver.Solve(observation, floor, scratch: scratch));
        Assert.Same(buffer, scratch.EdgeMaskBuffer);
    }

    [Fact]
    public void MissingOrCollapsedCompetitorCannotCreatePerfectMargin()
    {
        var spatial = new Vpsg3SpatialResult(1, 150, 150, 4, 4, true);
        var best = new Vpsg3RefinedCandidate(1, 0, 0, 1, 1, 1, spatial, 1);
        var scale = new Vpsg3ScaleResult(Vpsg3ScaleStatus.Success, 1, 3, 0, "");
        foreach (var runner in new Vpsg3RefinedCandidate?[] { null, best, best with { OffsetX = 5 } })
        {
            var gate = Vpsg3VerificationGate.EvaluateDecision(scale, best, runner,
                runner.HasValue, new(0, 0, 100, 100), 800, 600);
            Assert.False(gate.Passed);
            Assert.False(gate.HasDistinctRunnerUp);
            Assert.Equal("NoDistinctRefinedRunnerUp", gate.FailureReason);
        }
    }

    [Fact]
    public async Task ShadowUsesOwnedFrameAndExactFloorLeaseWithoutChangingBaseline()
    {
        var sha = new string('a', 64);
        var asset = new PrebuiltStructureLineAsset
        {
            FileName = "line.png", Sha256 = sha, SourceSha256 = sha, Width = 160, Height = 120,
            FileLength = 1, AlgorithmId = "test", AlgorithmFileName = "test.idva",
            AlgorithmSha256 = sha, AlgorithmSchemaVersion = "1"
        };
        var map = new MapRecord
        {
            Id = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UnixEpoch,
            Floors = [new() { Key = "1f", RecognitionSha256 = sha, PrebuiltStructureLine = asset }]
        };
        var key = new Vpsg3IndexCacheKey(map.Id, "1f", MapFeatureCacheRules.ComputeContentFingerprint(map),
            map.UpdatedAt, Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(asset));
        using var line = new Mat(120, 160, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(line, new Rect(10, 10, 140, 100), Scalar.White, 3);
        using var service = new MapCvRecognitionService(new MapRepository());
        var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(line, key);
        Assert.True(service.Vpsg3Registry.TryBeginBuild(key));
        Assert.True(service.Vpsg3Registry.TryPublishFloor(key, floor));
        var logPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../scratch/vpsg3-shadow-check"));
        await using var log = new MapLogCollector(new MapLogRepository(logPath)) { IsEnabled = true };
        var baseline = new MapOverlayTransform { ScaleX = 1.2, ScaleY = 1.2, OffsetX = 123, OffsetY = 456 };
        using var frame = new CapturedGameFrame(new Mat(120, 160, MatType.CV_8UC3, Scalar.Black),
            new(0, 0, 160, 120), new(25, 30, 160, 120), IntPtr.Zero);
        var work = service.QueueVpsg3Shadow(frame, map, "1f", baseline, log);
        frame.Dispose();
        await work;
        Assert.Contains(log.GetEntries(), e => e.Message == "VPSG3 shadow result");
        Assert.Equal(123d, baseline.OffsetX);
        Assert.True(service.Vpsg3Registry.Contains(map.Id, "1f"));

        map.UpdatedAt = map.UpdatedAt.AddSeconds(1);
        await service.QueueVpsg3Shadow(frame, map, "1f", baseline, log);
        Assert.Contains(log.GetEntries(), e => e.Message == "VPSG3 shadow skipped: index not ready");
        await service.QueueVpsg3Shadow(frame, map, "2f", baseline, log);
        Assert.Contains(log.GetEntries(), e => e.Message == "VPSG3 shadow skipped: prebuilt unavailable");
    }
}
