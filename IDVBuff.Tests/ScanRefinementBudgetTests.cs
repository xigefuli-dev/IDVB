using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class SideEntranceScanPipelineTests
{
    [Theory]
    [InlineData(860, false, true)]
    [InlineData(1001, false, false)]
    [InlineData(860, true, false)]
    public void RefinementRetainsEvaluatedPosesAtReserveButRejectsExpiredOrCancelledWork(
        int elapsedAfterFirstScale, bool cancel, bool expected)
    {
        using var scene = new StructuralScanScene();
        using var frame = new ScanFrameEvidence(scene.Frame, scene.Viewport, [scene.Gate],
            ScanExecutionPolicy.For(ScanPerformanceMode.Balanced));
        var clock = new ScanTestClock();
        using var cancellation = new CancellationTokenSource();
        var checkpoints = 0;
        using var context = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced, cancellation.Token,
            isCurrent: () =>
            {
                if (++checkpoints == 2)
                {
                    clock.Milliseconds = elapsedAfterFirstScale;
                    if (cancel) cancellation.Cancel();
                }
                return true;
            }, timeProvider: clock);
        var seed = new SideEntranceScanCandidate
        {
            Map = scene.Map, FloorKey = "1f", MatchScale = 1 / .985,
            StructureIndex = ScanStructureIndex.Get(scene.Line).WithScanAnchor(scene.Map, "1f"),
            AssociatedGate = scene.Gate
        };
        var refined = SideEntranceScanPipeline.RefineIdentityPose(seed, frame, scene.Viewport, context);
        Assert.Equal(expected, refined is not null);
        if (refined is null) return;
        Assert.Equal(140, context.RemainingMilliseconds);
        Assert.Equal(ScanIdentityState.Supported, refined.IdentityEvidence.State);
        Assert.Equal(SideEntranceCandidateDisposition.NeedsVerification, refined.Disposition);
        Assert.Null(ScanIdentityVerifier.SelectIdentity([refined], true, context.CanCompute));
        Assert.True(SideEntranceScanPipeline.TryCreateAlignmentSeed(refined, scene.Viewport, out var alignedSeed, out _));
        Assert.Equal(ScanIdentityState.Supported, ScanIdentityVerifier.Verify(frame, seed.StructureIndex!,
            alignedSeed.LockedTransform, scene.Viewport, context).State);
    }

    private sealed class ScanTestClock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
