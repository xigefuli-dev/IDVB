using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "13")]
public sealed class NoDoorAlignmentDeadlineTests
{
    [Fact]
    public async Task CancellationAfterWorkerStartsStopsFollowingAlignmentStage()
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followingStages = 0;
        var worker = NoDoorAlignmentDeadline.RunAsync(() =>
        {
            Assert.NotNull(NoDoorAlignmentDeadline.Current);
            Assert.Equal(int.MaxValue, MapNoDoorAlignmentBudgetContext.RemainingMilliseconds);
            started.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            if (NoDoorAlignmentDeadline.Current!.CanStartStage()) followingStages++;
            return MapNoDoorAlignmentBudgetContext.RemainingMilliseconds;
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        finally { release.Set(); }
        Assert.Equal(0, await worker);
        Assert.Equal(0, followingStages);
        Assert.Null(NoDoorAlignmentDeadline.Current);
        Assert.Null(MapNoDoorAlignmentBudgetContext.RemainingMilliseconds);
    }

    [Fact]
    public async Task FailedWorkerRestoresParentAmbientAndNextWorkerGetsFreshScope()
    {
        using var parent = new NoDoorAlignmentDeadline(default, 1000, enforceTimeBudget: false);
        using var ambient = parent.EnterAmbient();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<InvalidOperationException>(() => NoDoorAlignmentDeadline.RunAsync<int>(() =>
        {
            Assert.NotSame(parent, NoDoorAlignmentDeadline.Current);
            cancellation.Cancel();
            Assert.Equal(0, MapNoDoorAlignmentBudgetContext.RemainingMilliseconds);
            throw new InvalidOperationException("alignment failed");
        }, cancellation.Token));
        Assert.Same(parent, NoDoorAlignmentDeadline.Current);
        Assert.Equal(int.MaxValue, MapNoDoorAlignmentBudgetContext.RemainingMilliseconds);
        Assert.True(await NoDoorAlignmentDeadline.RunAsync(() =>
            NoDoorAlignmentDeadline.Current!.CanStartStage(), default));
        Assert.Same(parent, NoDoorAlignmentDeadline.Current);
    }

    [Fact]
    public async Task CancelledQueuedWorkNeverEntersAlignment()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var entered = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoDoorAlignmentDeadline.RunAsync(() =>
        {
            entered = true;
            return 0;
        }, cancellation.Token));
        Assert.False(entered);
    }
}
