using System.Diagnostics;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class OverlayRealtimeTransformBudgetTests
{
    [Fact]
    public void ThousandTranslationUpdates_DoNotRequestBitmapRebuild_OrAllocate()
    {
        const float width = 1365.25f;
        const float height = 1018.75f;
        for (var index = 0; index < 100; index++)
            Assert.False(MapOverlayRealtimeTransformPlanner.RequiresBitmapRebuild(
                width, height, width, height));

        var timings = new double[1000];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < timings.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var rebuild = MapOverlayRealtimeTransformPlanner.RequiresBitmapRebuild(
                width, height, width, height);
            timings[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.False(rebuild);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(timings);
        var p95 = timings[(int)Math.Ceiling(timings.Length * 0.95d) - 1];

        Assert.True(allocated < 4096, $"Hot path allocated {allocated} bytes.");
        Assert.True(p95 < 8.33d, $"P95 {p95:F3}ms exceeded the 120Hz budget.");
    }

    [Fact]
    public void LatestOnlyPublisher_CoalescesBeforeDispatcherRuns()
    {
        Action? scheduled = null;
        var applied = new List<RealtimeTransformState>();
        var publisher = new RealtimeMapTransformPublisher(
            action =>
            {
                scheduled = action;
                return true;
            },
            state =>
            {
                applied.Add(state);
                return true;
            });

        for (var index = 0; index < 1000; index++)
        {
            publisher.Publish(new RealtimeTransformState(
                1d, index, -index, index, 0.9d, 7));
        }

        Assert.NotNull(scheduled);
        scheduled!();
        var metrics = publisher.Snapshot(reset: false);
        Assert.Single(applied);
        Assert.Equal(999d, applied[0].Tx);
        Assert.Equal(1000, metrics.PublishedTransforms);
        Assert.Equal(999, metrics.CoalescedTransforms);
        Assert.Equal(1, metrics.AppliedTransforms);
    }

    [Fact]
    public void HiddenTransformBuffer_RetainsOnlyLatestWithoutDispatching()
    {
        var buffer = new LatestRealtimeTransformBuffer();

        for (var index = 0; index < 1000; index++)
        {
            buffer.Hold(new RealtimeTransformState(
                1d, index, -index, index, 0.9d, 7));
        }

        Assert.True(buffer.TryTake(out var latest));
        Assert.Equal(999d, latest.Tx);
        Assert.Equal(-999d, latest.Ty);
        Assert.False(buffer.TryTake(out _));
    }

    [Fact]
    public void HiddenTransformBuffer_DoesNotDiscardNewerStateFromNextHideCycle()
    {
        var buffer = new LatestRealtimeTransformBuffer();
        var previous = new RealtimeTransformState(1d, 10d, -10d, 10, 0.9d, 7);
        var newer = new RealtimeTransformState(1d, 11d, -11d, 11, 0.9d, 7);

        buffer.Hold(previous);
        buffer.Hold(newer);
        buffer.DiscardIfLatest(previous);

        Assert.True(buffer.TryTake(out var latest));
        Assert.Equal(newer, latest);
    }
}
