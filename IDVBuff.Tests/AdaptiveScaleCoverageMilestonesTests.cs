using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;

public sealed class AdaptiveScaleCoverageMilestonesTests
{
    [Fact]
    public void CoverageUsesEntireReferenceContourAndPhysicalViewportTransform()
    {
        var reference = Enumerable.Range(0, 100).Select(i => new Point(i * 10, 20)).ToArray();
        using var live = new Mat(100, 400, MatType.CV_8UC1, Scalar.Black);
        // Only the first twenty of one hundred reference points are revealed.
        for (var i = 0; i < 20; i++)
            live.Set(40, i * 20, (byte)255);
        var transform = new MapOverlayTransform { ScaleX = 2, ScaleY = 2, OffsetX = 420, OffsetY = 260 };
        var hits = AdaptiveScaleCoverageMilestones.CountCoveredPoints(reference, live, transform,
            new MapScreenRect(420, 260, 400, 100));
        Assert.Equal(20, hits);
        var state = new AdaptiveScaleCoverageMilestones();
        state.Observe((double)hits / reference.Length, false);
        Assert.Equal(20, state.ReachedPercent);
        live.SetTo(Scalar.Black);
        Assert.Equal(0, AdaptiveScaleCoverageMilestones.CountCoveredPoints(reference, live, transform,
            new MapScreenRect(420, 260, 400, 100)));
    }

    [Fact]
    public void MasterSwitchBlocksTrustedAndExplicitLocksIncludingExistingBaseline()
    {
        var options = new AdaptiveScaleOptions();
        var controller = new AdaptiveScaleController(new(Guid.NewGuid(), 1, "1f", 1920, 1080, 800, 600), options);
        controller.BeginOrResumeOpen(1, 0.9433962264150942, null, true);
        Assert.True(controller.IsReliable);
        options.ScaleLockingEnabled = false;
        Assert.False(controller.IsReliable);
        Assert.False(controller.HasReliableBaseline);
        Assert.False(controller.LockCurrentScale(1.06));
        controller.EndOpen(1);
        controller.BeginOrResumeOpen(2, 1.15, 0.94, true);
        Assert.False(controller.IsReliable);
    }

    [Fact]
    public void CrossingSchedules15And25PercentRefreshesWithoutRepeating()
    {
        var state = new AdaptiveScaleCoverageMilestones();
        state.Observe(0.10, false);
        Assert.Equal(10, state.ReachedPercent);
        Assert.True(state.RefreshPending);
        state.Observe(0.14, true);
        Assert.False(state.RefreshPending);

        state.Observe(0.15, false);
        Assert.Equal(15, state.ReachedPercent);
        Assert.True(state.RefreshPending);
        state.Observe(0.16, true);
        Assert.False(state.RefreshPending);

        state.Observe(0.20, false);
        Assert.Equal(20, state.ReachedPercent);
        Assert.True(state.RefreshPending);
        state.Observe(0.24, true);
        Assert.False(state.RefreshPending);

        state.Observe(0.249, false);
        Assert.False(state.RefreshPending);
        state.Observe(0.25, false);
        Assert.Equal(25, state.ReachedPercent);
        Assert.True(state.RefreshPending);
    }

    [Fact]
    public void CoverageRegressionAfter15PercentDoesNotRearm10Or15Percent()
    {
        var state = new AdaptiveScaleCoverageMilestones();
        state.Observe(0.15, false);
        Assert.Equal(15, state.ReachedPercent);
        Assert.True(state.RefreshPending);

        // The refresh succeeds while this frame sees less structure.  The
        // monotonic milestone remains at 15%, rather than falling back to 10%
        // or scheduling 15% again when coverage later recovers.
        state.Observe(0.06, true);
        Assert.Equal(15, state.ReachedPercent);
        Assert.False(state.RefreshPending);
        state.Observe(0.14, false);
        Assert.False(state.RefreshPending);
        state.Observe(0.15, false);
        Assert.False(state.RefreshPending);
        state.Observe(0.20, false);
        Assert.Equal(20, state.ReachedPercent);
        Assert.True(state.RefreshPending);
    }

    [Theory]
    [InlineData(0.34, 30)]
    [InlineData(0.50, 50)]
    [InlineData(0.90, 50)]
    public void FirstAlignmentCanSkipMilestones(double coverage, int expected)
    {
        var state = new AdaptiveScaleCoverageMilestones();
        state.Observe(coverage, false);
        Assert.Equal(expected, state.ReachedPercent);
        Assert.True(state.RefreshPending);
        state.Observe(coverage, true);
        Assert.False(state.RefreshPending);
    }

    [Fact]
    public void RefreshCanCrossAnotherMilestoneAndCoverageRegressionDoesNotRearm()
    {
        var state = new AdaptiveScaleCoverageMilestones();
        state.Observe(0.12, false);
        state.Observe(0.41, true);
        Assert.Equal(40, state.ReachedPercent);
        Assert.True(state.RefreshPending);
        state.Observe(0.08, true);
        state.Observe(0.42, false);
        Assert.False(state.RefreshPending);
        state.Observe(0.51, false);
        Assert.True(state.RefreshPending);
    }

    [Fact]
    public void FailedRefreshAndOtherFloorCannotConsumePendingMilestone()
    {
        var first = new AdaptiveScaleCoverageMilestones();
        var second = new AdaptiveScaleCoverageMilestones();
        first.Observe(0.23, false);
        first.Observe(0.23, false);
        second.Observe(0.55, true);
        Assert.True(first.RefreshPending);
        Assert.Equal(20, first.ReachedPercent);
        Assert.Equal(50, second.ReachedPercent);
        Assert.Equal(0, new AdaptiveScaleCoverageMilestones().ReachedPercent);
    }
}
