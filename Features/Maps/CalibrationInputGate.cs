using System.Diagnostics;

namespace IDVBuff.Features.Maps;

// Input timestamps are captured at the key edge, before dispatcher queuing.
// Ending calibration requires a new edge; rejected input is never replayed.
internal sealed class CalibrationInputGate
{
    private int _active;
    private long _boundary;

    public bool IsActive => Volatile.Read(ref _active) > 0;
    public bool Reject(long? timestamp = null) => IsActive
        || timestamp is { } captured && captured <= Interlocked.Read(ref _boundary);

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        Interlocked.Exchange(ref _boundary, Stopwatch.GetTimestamp());
        return new Guard(this);
    }

    private sealed class Guard(CalibrationInputGate owner) : IDisposable
    {
        private CalibrationInputGate? _owner = owner;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _owner, null);
            if (gate is null) return;
            Interlocked.Exchange(ref gate._boundary, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref gate._active);
        }
    }
}
