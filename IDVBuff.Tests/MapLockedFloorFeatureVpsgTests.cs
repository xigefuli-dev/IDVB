using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[Collection(CompleteAlignmentTestCollection.Name)]
public sealed class MapLockedFloorFeatureVpsgTests
{
    [Fact]
    public async Task LockedFloorVpsg_RecoversScaleFromWrongCrossFloorSeed()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        const double expectedScale = 1.3375d;
        var viewport = new MapScreenRect(
            420d,
            260d,
            720d * expectedScale,
            540d * expectedScale);
        using var frame = scenario.FloorFrameScaled(
            CompleteAlignmentTestScenario.UpperFloor,
            expectedScale,
            viewport);
        // 跨楼层 seed 复用错误：KEEP-1.0 场景下 2F 沿用 1F 的屏幕 scale（ScaleX=1.0）。
        var wrongSeed = scenario.FloorScaleSeed(
            CompleteAlignmentTestScenario.UpperFloor);
        using var alignmentBudget = MapNoDoorAlignmentBudgetContext.Enter(
            () => 5_000);

        var attempt = scenario.Service.AlignLockedFloorFeature(
            frame,
            scenario.Map.Id,
            CompleteAlignmentTestScenario.UpperFloor,
            wrongSeed,
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning,
            identityPriorConfidence: 0d);

        var recognition = Assert.IsType<RuntimeMapRecognition>(
            attempt.Recognition);
        Assert.True(attempt.Diagnostics.ScaleBootstrapSucceeded);
        Assert.True(attempt.Diagnostics.ScaleBootstrapValidated);
        Assert.True(attempt.StructureAccepted, attempt.StructureFailureReason);
        var transform = Assert.IsType<MapOverlayTransform>(
            recognition.Result.OverlayTransform);
        Assert.InRange(
            transform.ScaleX,
            expectedScale - 0.02d,
            expectedScale + 0.02d);
        Assert.InRange(Math.Abs(transform.OffsetX - viewport.X), 0d, 2d);
        Assert.InRange(Math.Abs(transform.OffsetY - viewport.Y), 0d, 2d);
        Assert.True(
            attempt.Diagnostics.ScaleBootstrapUniqueMatches
                >= MapVpsgScaleEstimator.MinimumUniqueMatches);
        Assert.True(
            attempt.Diagnostics.ScaleBootstrapPairVotes
                >= MapVpsgScaleEstimator.MinimumPairVotes);
    }

    [Fact]
    public async Task LockedFloorVpsg_SiftFallbackOff_ReportsFailureCleanly()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        // 无结构重叠的随机噪声帧：VPSG 匹配不到参考上层，SIFT 回退默认关闭。
        using var noise = new Mat(new Size(800, 600), MatType.CV_8UC3);
        Cv2.Randu(noise, Scalar.All(0), Scalar.All(255));
        using var frame = new CapturedGameFrame(
            noise.Clone(),
            DisplayTestMatrix.Baseline.PhysicalBounds,
            new MapScreenRect(100d, 80d, 800d, 600d),
            IntPtr.Zero);
        using var alignmentBudget = MapNoDoorAlignmentBudgetContext.Enter(
            () => 5_000);

        var attempt = scenario.Service.AlignLockedFloorFeature(
            frame,
            scenario.Map.Id,
            CompleteAlignmentTestScenario.UpperFloor,
            scenario.FloorScaleSeed(CompleteAlignmentTestScenario.UpperFloor),
            MapOverlayAlignmentMode.Uniform,
            CompleteAlignmentTestScenario.RecognitionTuning,
            CompleteAlignmentTestScenario.StructureTuning,
            identityPriorConfidence: 0d);

        Assert.Null(attempt.Recognition);
        Assert.True(attempt.Diagnostics.ScaleBootstrapAttempted);
        Assert.False(attempt.Diagnostics.ScaleBootstrapSucceeded);
        Assert.False(attempt.Diagnostics.ScaleBootstrapValidated);
    }
}
