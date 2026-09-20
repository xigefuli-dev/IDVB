using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using Xunit;
using Xunit.Abstractions;

namespace IDVBuff.Tests.Vpsg3_5;

public sealed class Vpsg3_5TrackingPhase3Tests
{
    private readonly ITestOutputHelper _output;

    public Vpsg3_5TrackingPhase3Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Test1_Vpsg3_5TrackingConfig_HasDefaultHybridSettings()
    {
        var config = Vpsg3_5TrackingConfig.Default;
        Assert.True(config.EnableMouseFeedforward);
        Assert.Equal(1.0d, config.MouseScaleRatio);
        Assert.Equal(33, config.VisualVerificationIntervalMs);
        Assert.Equal(12.0d, config.BoundaryCollisionThresholdPixels);
        Assert.Equal(0.2d, config.ResidualCorrectionAlpha);
        Assert.Equal(120.0d, config.MaxTranslationJumpPixels);
    }

    [Fact]
    public void Test2_MouseFeedforward_DeltaIntegration_AndBoundaryClamping()
    {
        // Simulate mouse feedforward with boundary clamping
        var config = Vpsg3_5TrackingConfig.Default;
        var feedforwardTx = 100.0d;
        var feedforwardTy = 200.0d;
        var isClampedMaxX = false;
        var isClampedMinX = false;

        // Step 1: Normal mouse drag
        var rawDx = 10.0d * config.MouseScaleRatio;
        var rawDy = 5.0d * config.MouseScaleRatio;
        feedforwardTx += rawDx;
        feedforwardTy += rawDy;

        Assert.Equal(110.0d, feedforwardTx);
        Assert.Equal(205.0d, feedforwardTy);

        // Step 2: Wall collision detected at X = 130.0
        feedforwardTx += 20.0d; // mouse moved to 130
        var lastCaptureTx = 130.0d;
        feedforwardTx += 15.0d; // mouse continued to 145, but visual map stayed at 130

        var mouseDeltaX = feedforwardTx - lastCaptureTx; // 15.0
        var visualDeltaX = 0.0d; // visual map did not move
        if (Math.Abs(mouseDeltaX) >= config.BoundaryCollisionThresholdPixels && Math.Abs(visualDeltaX) <= 2.0d)
        {
            if (mouseDeltaX > 0) isClampedMaxX = true;
            else isClampedMinX = true;
            feedforwardTx = 130.0d; // clamped back to wall
        }

        Assert.True(isClampedMaxX);
        Assert.False(isClampedMinX);
        Assert.Equal(130.0d, feedforwardTx);

        // Step 3: Mouse tries to move further right (dx > 0) -> blocked by clamp
        var nextDx = 10.0d;
        if (isClampedMaxX && nextDx > 0) nextDx = 0.0d;
        feedforwardTx += nextDx;
        Assert.Equal(130.0d, feedforwardTx);

        // Step 4: Mouse moves left (pulling away from the wall) -> unclamps immediately
        var reverseDx = -5.0d;
        if (isClampedMaxX && reverseDx < -1.0d) isClampedMaxX = false;
        if (isClampedMaxX && reverseDx > 0) reverseDx = 0.0d;
        feedforwardTx += reverseDx;

        Assert.False(isClampedMaxX);
        Assert.Equal(125.0d, feedforwardTx);
    }

    [Fact]
    public void Test3_ResidualCorrectionEMA_AbsorbsSmallDriftSmoothly()
    {
        var config = Vpsg3_5TrackingConfig.Default;
        var feedforwardTx = 300.0d;
        var visualObservedTx = 302.0d; // 2px visual residual

        // Simulate 5 frames of low-speed drift absorption
        for (int i = 0; i < 5; i++)
        {
            var errX = visualObservedTx - feedforwardTx;
            feedforwardTx += errX * config.ResidualCorrectionAlpha;
        }

        // After 5 EMA steps: 300 + 2 * (1 - (1 - 0.2)^5) = 300 + 2 * (1 - 0.32768) = 301.34464
        Assert.InRange(feedforwardTx, 301.2d, 301.5d);
    }

    [Fact]
    public void Test4_ReleaseSnap_ReplacesTransformWithGroundTruth()
    {
        var mapId = Guid.NewGuid();
        var map = new MapRecord { Id = mapId, UpdatedAt = DateTimeOffset.UtcNow };
        const double lockedScale = 0.85d;
        var feedforwardTx = 450.5d;
        var feedforwardTy = 320.2d;

        var feedforwardTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale,
            lockedScale,
            feedforwardTx,
            feedforwardTy,
            1000,
            1000,
            residualPixels: 0d,
            orientationDegrees: 0,
            alignmentMode: MapOverlayAlignmentMode.Uniform);

        var recognition = new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = "path/to/floor.png",
            Result = new MapRecognitionResult
            {
                MapId = mapId,
                Floor = "1f",
                Confidence = 0.95d,
                Source = MapRecognitionSource.VpsgTracking,
                OverlayTransform = feedforwardTransform
            }
        };

        // Release Snap returns ground truth at (450.0, 320.0)
        const double trueTx = 450.0d;
        const double trueTy = 320.0d;
        var snapTransform = MapCanonicalTransformMath.BuildOverlayTransform(
            lockedScale,
            lockedScale,
            trueTx,
            trueTy,
            1000,
            1000,
            residualPixels: 0d,
            orientationDegrees: 0,
            alignmentMode: MapOverlayAlignmentMode.Uniform);

        var finalRecognition = MapCvRecognitionBuilders.ReplaceTransformAndSource(
            recognition,
            snapTransform,
            MapRecognitionSource.VpsgTracking);

        Assert.Equal(trueTx, finalRecognition.Result.OverlayTransform!.OffsetX);
        Assert.Equal(trueTy, finalRecognition.Result.OverlayTransform!.OffsetY);
        Assert.Equal(MapRecognitionSource.VpsgTracking, finalRecognition.Result.Source);
    }

    [Fact]
    public void Test5_TranslationJumpTooLarge_DistanceExceedsGate_IsRejected()
    {
        var config = Vpsg3_5TrackingConfig.Default;
        const double priorTx = 200.0d;
        const double priorTy = 300.0d;

        // Simulate a false positive jump to 450.0 (distance = 250px > 80px)
        const double candidateTx = 450.0d;
        const double candidateTy = 300.0d;
        var dDist = Math.Sqrt(Math.Pow(candidateTx - priorTx, 2) + Math.Pow(candidateTy - priorTy, 2));

        Assert.True(dDist > config.MaxTranslationJumpPixels);

        // A jump of 250px must be rejected
        var timing = new Vpsg3_5TrackingTiming(1.0, 2.0, 0.5, 0.5, 0.1, 4.1);
        var result = dDist > config.MaxTranslationJumpPixels
            ? Vpsg3_5TrackingResult.Reject(
                $"TranslationJumpTooLarge(Distance={dDist:F1}px,Max={config.MaxTranslationJumpPixels:F1}px)",
                0.85d,
                priorTx,
                priorTy,
                timing)
            : Vpsg3_5TrackingResult.Accept(0.85d, candidateTx, candidateTy, 0.9, 0.9, 4, false, timing);

        Assert.False(result.IsAccepted);
        Assert.Equal(priorTx, result.OffsetX);
        Assert.Equal(priorTy, result.OffsetY);
        Assert.Contains("TranslationJumpTooLarge", result.FallbackReason);
    }
}
