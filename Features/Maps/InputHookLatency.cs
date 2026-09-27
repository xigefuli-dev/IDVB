using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>Allocation-free counters for the OS hook thread. No logging or locks here.</summary>
internal sealed class InputHookLatency
{
    private long _count, _delayed, _maxArrivalMilliseconds, _maxCallbackTicks;

    public void Record(uint eventMilliseconds, uint arrivalMilliseconds, long callbackTicks)
    {
        Interlocked.Increment(ref _count);
        // Native input time and TickCount are 32-bit uptime values. Subtraction
        // must survive wraparound; future/invalid timestamps are not latency.
        var delay = unchecked(arrivalMilliseconds - eventMilliseconds);
        if (delay <= int.MaxValue)
        {
            Max(ref _maxArrivalMilliseconds, delay);
            if (delay >= 50) Interlocked.Increment(ref _delayed);
        }
        Max(ref _maxCallbackTicks, Math.Max(0, callbackTicks));
    }

    // Fields are sampled independently: a callback at the reporting boundary
    // may contribute its count and maximum to adjacent windows.
    public Snapshot Take() => new(
        Interlocked.Exchange(ref _count, 0), Interlocked.Exchange(ref _delayed, 0),
        Interlocked.Exchange(ref _maxArrivalMilliseconds, 0),
        Interlocked.Exchange(ref _maxCallbackTicks, 0) * 1000d / Stopwatch.Frequency);

    private static void Max(ref long target, long value)
    {
        var previous = Volatile.Read(ref target);
        while (value > previous)
        {
            var observed = Interlocked.CompareExchange(ref target, value, previous);
            if (observed == previous) return;
            previous = observed;
        }
    }

    internal readonly record struct Snapshot(long Count, long Delayed50Milliseconds,
        long MaximumArrivalMilliseconds, double MaximumCallbackMilliseconds);
}
