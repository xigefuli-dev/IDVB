using IDVBuff.Features.Maps;
using System.Runtime.InteropServices;

namespace IDVBuff.Tests;

public sealed class CaptureStreamWorkerTests
{
    [Fact]
    public async Task CaptureOwnerDoesNotRetainTheCallingScansDeadlineOrFrame()
    {
        using var scan = ScanExecutionContext.Enter(ScanPerformanceMode.Balanced);
        ScanExecutionContext? inherited = scan;
        await CaptureStreamWorker.RunAsync(() => inherited = ScanExecutionContext.Current)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(inherited);
        Assert.Same(scan, ScanExecutionContext.Current);
    }

    [Fact]
    public async Task StaRescanClosesResourceOnItsCreatingMtaThread()
    {
        var ownerThread = 0;
        var releaseThread = 0;
        await CaptureStreamWorker.RunAsync(() =>
        {
            ownerThread = Environment.CurrentManagedThreadId;
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            Assert.True(CaptureStreamWorker.HasThreadAccess);
        });
        var requested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            requested.TrySetResult(CaptureStreamWorker.RunAsync(() =>
            {
                releaseThread = Environment.CurrentManagedThreadId;
                if (releaseThread != ownerThread)
                    throw new COMException("Wrong capture thread", unchecked((int)0x8001010E));
            }));
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        await (await requested.Task.WaitAsync(TimeSpan.FromSeconds(5))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ownerThread, releaseThread);
    }

    [Fact]
    public async Task SlowShutdownDoesNotBlockCallerOrLetReplacementOvertakeIt()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var close = CaptureStreamWorker.RunAsync(() =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            order.Add("closed");
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = CaptureStreamWorker.RunAsync(() => order.Add("created"));
            Assert.False(close.IsCompleted);
            Assert.False(replacement.IsCompleted);
            release.Set();
            await Task.WhenAll(close, replacement).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "closed", "created" }, order);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task FailedNativeOperationDoesNotStopLaterCaptureWork()
    {
        var failure = CaptureStreamWorker.RunAsync(() =>
            throw new COMException("Device closed", unchecked((int)0x8001010E)));
        await Assert.ThrowsAsync<COMException>(() => failure);
        var ran = false;
        await CaptureStreamWorker.RunAsync(() => ran = true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran);
    }
}
