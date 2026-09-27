using IDVBuff.Features.Maps;
using System.Diagnostics;

namespace IDVBuff.Tests;

public sealed class InputHookLatencyTests
{
    [Fact]
    public void MeasuresArrivalSeparatelyFromOurCallbackAndResetsWindow()
    {
        var meter = new InputHookLatency();
        meter.Record(100, 180, Stopwatch.Frequency / 100);
        meter.Record(200, 202, Stopwatch.Frequency / 1000);
        var result = meter.Take();
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result.Delayed50Milliseconds);
        Assert.Equal(80, result.MaximumArrivalMilliseconds);
        Assert.InRange(result.MaximumCallbackMilliseconds, 9.99, 10.01);
        Assert.Equal(default, meter.Take());
    }

    [Fact]
    public void NativeTimestampWrapAndFutureTimestampDoNotProduceHugeDelay()
    {
        var meter = new InputHookLatency();
        meter.Record(uint.MaxValue - 4, 5, 0);
        meter.Record(200, 199, 0);
        var result = meter.Take();
        Assert.Equal(10, result.MaximumArrivalMilliseconds);
        Assert.Equal(0, result.Delayed50Milliseconds);
    }

    [Fact]
    public void EventRecordingDoesNotAllocate()
    {
        var meter = new InputHookLatency();
        for (var i = 0; i < 1000; i++) meter.Record(1, 2, i);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) meter.Record(1, 2, i);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(11000, meter.Take().Count);
    }
}
