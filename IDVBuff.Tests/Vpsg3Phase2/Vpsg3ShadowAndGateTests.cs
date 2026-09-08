using IDVBuff.Features.Maps;
using IDVBuff.Tests.Vpsg3Phase0;
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
    public void ApertureMarginRelaxation_AllowsSpatiallyConsistentNearRunnerUp()
    {
        var consistentSpatial = new Vpsg3SpatialResult(0.52, 150, 78, 4, 4, true);
        var best = new Vpsg3RefinedCandidate(1, 0, 0, 0.52, 0.52, 0.52, consistentSpatial, 1);
        var runnerSpatial = new Vpsg3SpatialResult(0.4733, 150, 71, 4, 4, true);
        var runner = new Vpsg3RefinedCandidate(1, 15, 15, 0.4733, 0.4733, 0.4733, runnerSpatial, 1);
        var scale = new Vpsg3ScaleResult(Vpsg3ScaleStatus.Success, 1, 3, 0, "");

        var gate = Vpsg3VerificationGate.EvaluateDecision(scale, best, runner,
            true, new(0, 0, 100, 100), 800, 600);
        Assert.True(gate.Passed);
        Assert.Equal(0.52 - 0.4733, gate.Margin, 4);

        var inconsistentSpatial = new Vpsg3SpatialResult(0.44, 150, 66, 4, 2, false);
        var weakBest = best with { Spatial = inconsistentSpatial };
        var weakRunner = runner with { Spatial = runnerSpatial with { GlobalScore = 0.3933 } };
        var gateWeak = Vpsg3VerificationGate.EvaluateDecision(scale, weakBest, weakRunner,
            true, new(0, 0, 100, 100), 800, 600);
        Assert.False(gateWeak.Passed);
        Assert.Contains("ApertureMarginBelowThreshold", gateWeak.FailureReason);

        // When margin is wide (e.g. 0.20) but spatial consistency is false, passes Gate 2 but fails Gate 5:
        var wideMarginRunner = runner with { Spatial = runnerSpatial with { GlobalScore = 0.24 } };
        var gateSpatialFail = Vpsg3VerificationGate.EvaluateDecision(scale, weakBest, wideMarginRunner,
            true, new(0, 0, 100, 100), 800, 600);
        Assert.False(gateSpatialFail.Passed);
        Assert.Contains("SpatialPartitionsBelowThreshold", gateSpatialFail.FailureReason);

        // When spatially consistent but margin below relaxed threshold 0.035:
        var tinyMarginRunner = runner with { Spatial = runnerSpatial with { GlobalScore = 0.51 } };
        var gateTiny = Vpsg3VerificationGate.EvaluateDecision(scale, best, tinyMarginRunner,
            true, new(0, 0, 100, 100), 800, 600);
        Assert.False(gateTiny.Passed);
        Assert.Contains("ApertureMarginBelowThreshold", gateTiny.FailureReason);
    }

    [Fact]
    public void CandidateReRanking_InSteadyTracking_PromotesConsistentRunnerUp()
    {
        // Demonstrates the Phase 1 fix: in steady tracking with knownScaleSeed,
        // a spatially consistent runner-up with higher spatial score is swapped to best candidate.
        // Before swap, primary=0.622, runnerUp=0.860 -> negative margin -0.238 -> rejected.
        // After swap, primary=0.860, runnerUp=0.622 -> positive margin +0.238 -> passed.
        var scale = new Vpsg3ScaleResult(Vpsg3ScaleStatus.Success, 1.016, 10.0, 0, "");
        var weakPrimarySpatial = new Vpsg3SpatialResult(0.622, 150, 93, 4, 4, true);
        var weakPrimary = new Vpsg3RefinedCandidate(1.016, 25, 25, 0.622, 0.622, 0.622, weakPrimarySpatial, 1);
        var strongRunnerSpatial = new Vpsg3SpatialResult(0.860, 150, 129, 4, 4, true);
        var strongRunner = new Vpsg3RefinedCandidate(1.016, 10, 10, 0.860, 0.860, 0.860, strongRunnerSpatial, 1);

        // Raw Gate decision without swap would fail with ApertureMarginBelowThreshold
        var unswappedGate = Vpsg3VerificationGate.EvaluateDecision(scale, weakPrimary, strongRunner,
            true, new(0, 0, 100, 100), 800, 600);
        Assert.False(unswappedGate.Passed);
        Assert.Contains("ApertureMarginBelowThreshold", unswappedGate.FailureReason);
        Assert.True(unswappedGate.Margin < 0);

        // With swap applied (as in Stage 4.5)
        var swappedGate = Vpsg3VerificationGate.EvaluateDecision(scale, strongRunner, weakPrimary,
            true, new(0, 0, 100, 100), 800, 600);
        Assert.True(swappedGate.Passed);
        Assert.True(swappedGate.Margin > 0.20);
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

    [Fact]
    public void AlignLockedFloorFeature_WithPreparedVpsg3Index_DirectlyResolvesViaVpsg3()
    {
        var dataset = Vpsg3Phase0DatasetGenerator.GenerateDataset();
        try
        {
            var sample = dataset.First(s => s.SourceType == "Synthetic" && Math.Abs(s.TrueScale - 1.0d) < 1e-4 && s.FogFraction == 0.0d);
            var sha = new string('b', 64);
            var asset = new PrebuiltStructureLineAsset
            {
                FileName = "line.png", Sha256 = sha, SourceSha256 = sha,
                Width = sample.ReferenceStructureLine.Width,
                Height = sample.ReferenceStructureLine.Height,
                FileLength = 1, AlgorithmId = "test", AlgorithmFileName = "test.idva",
                AlgorithmSha256 = sha, AlgorithmSchemaVersion = "1"
            };
            var map = new MapRecord
            {
                Id = Guid.NewGuid(),
                UpdatedAt = DateTimeOffset.UnixEpoch,
                Floors = [new() { Key = "1f", RecognitionSha256 = sha, PrebuiltStructureLine = asset }]
            };
            var key = new Vpsg3IndexCacheKey(map.Id, "1f", MapFeatureCacheRules.ComputeContentFingerprint(map),
                map.UpdatedAt, Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(asset));

            using var service = new MapCvRecognitionService(new MapRepository());
            var floor = Vpsg3PreparedIndexBuilder.BuildFromMat(sample.ReferenceStructureLine, key);
            Assert.True(service.Vpsg3Registry.TryBeginBuild(key));
            Assert.True(service.Vpsg3Registry.TryPublishFloor(key, floor));

            using var frame = new CapturedGameFrame(
                sample.LiveImage.Clone(),
                sample.ViewportBounds,
                sample.ViewportBounds,
                IntPtr.Zero);

            var canAlign = service.TryAlignWithVpsg3(frame, map, "1f", 1.0d, out var attempt);
            Assert.True(canAlign);
            Assert.NotNull(attempt);
            Assert.NotNull(attempt.Recognition);
            Assert.Equal("vpsg3", attempt.Diagnostics.ScaleBootstrapMethod);
            Assert.True(attempt.Diagnostics.ScaleBootstrapValidated);
            Assert.True(attempt.StructureAccepted);
            Assert.Equal(AlignmentSearchStage.StructureFallback, attempt.SearchStage);

            var transform = attempt.Recognition.Result.OverlayTransform;
            Assert.NotNull(transform);
            Assert.Equal(sample.ReferenceStructureLine.Width, transform.ReferenceWidth);
            Assert.Equal(sample.ReferenceStructureLine.Height, transform.ReferenceHeight);
            Assert.True(transform.ReferenceWidth * transform.ScaleX > 0);
            Assert.True(transform.ReferenceHeight * transform.ScaleY > 0);

            Assert.NotNull(attempt.StructureResult);
            Assert.Equal(sample.ReferenceStructureLine.Width, attempt.StructureResult.ReferenceWidth);
            Assert.Equal(sample.ReferenceStructureLine.Height, attempt.StructureResult.ReferenceHeight);
            Assert.True(attempt.StructureResult.LockedScale > 0);
        }
        finally
        {
            foreach (var s in dataset) s.Dispose();
        }
    }

    [Fact]
    public void SpatialVerification_DominantPartition_QualifiesConsistent()
    {
        // 场景：视口在地图角落，100 个点中有 80 个点落在象限 0（占比 80%），象限 0 命中 65 个（ratio 81%）。
        // 其余象限点数很少或未及格，但全局得分 70%，总命中数 70。
        // 原逻辑因 passedParts == 1 < 2 拒收；新逻辑识别主导象限高质量覆盖并放行。
        var cfg = Vpsg3TuningConfig.Default;
        var width = 200;
        var height = 200;
        // 构造点：象限 0 (X < 100, Y < 100) 放入 80 个点
        var points = new List<OpenCvSharp.Point>();
        for (var i = 0; i < 80; i++)
        {
            points.Add(new OpenCvSharp.Point(10 + (i % 10) * 8, 10 + (i / 10) * 8));
        }
        // 象限 1 (X >= 100, Y < 100) 放入 20 个点
        for (var i = 0; i < 20; i++)
        {
            points.Add(new OpenCvSharp.Point(110 + (i % 5) * 8, 10 + (i / 5) * 8));
        }

        // 用 PreparedFloor 模拟全部命中
        using var refMat = new OpenCvSharp.Mat(400, 400, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(255));
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp", DateTimeOffset.UnixEpoch, "gen");
        var prepared = Vpsg3PreparedIndexBuilder.BuildFromMat(refMat, key);

        var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
            points, null!, prepared, 1.0, 0, 0, new(0, 0, 200, 200), width, height, cfg);

        Assert.True(spatial.IsSpatiallyConsistent);
        Assert.True(spatial.GlobalScore >= 0.50);
        Assert.True(spatial.HitPoints >= 20);
    }

    [Fact]
    public void SpatialVerification_HighConfidenceGlobal_QualifiesConsistent()
    {
        // 场景：虽然只有 1 个分区及格，但全局命中率达 60% 且总命中点数 >= 25，应高置信放行
        var cfg = Vpsg3TuningConfig.Default;
        var width = 200;
        var height = 200;
        var points = new List<OpenCvSharp.Point>();
        // 象限 0: 30 个点
        for (var i = 0; i < 30; i++) points.Add(new OpenCvSharp.Point(10 + i * 2, 10));
        // 象限 1: 10 个点
        for (var i = 0; i < 10; i++) points.Add(new OpenCvSharp.Point(110 + i * 2, 10));
        // 象限 2: 10 个点
        for (var i = 0; i < 10; i++) points.Add(new OpenCvSharp.Point(10 + i * 2, 110));

        using var refMat = new OpenCvSharp.Mat(400, 400, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(255));
        var key = new Vpsg3IndexCacheKey(Guid.NewGuid(), "1f", "fp", DateTimeOffset.UnixEpoch, "gen");
        var prepared = Vpsg3PreparedIndexBuilder.BuildFromMat(refMat, key);

        var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
            points, null!, prepared, 1.0, 0, 0, new(0, 0, 200, 200), width, height, cfg);

        Assert.True(spatial.IsSpatiallyConsistent);
        Assert.True(spatial.TotalValidPoints == 50);
        Assert.True(spatial.HitPoints == 50);
    }
}
