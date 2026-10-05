namespace IDVBuff.Features.Maps;

/// <summary>One reusable device per capture owner; retired devices outlive active streams.</summary>
internal sealed class CaptureDeviceOwner<T>(Func<T> create) where T : class, IDisposable
{
    private readonly object _gate = new();
    private Entry? _current;

    internal Lease Rent()
    {
        lock (_gate)
        {
            _current ??= new Entry(create());
            _current.Retain();
            return new Lease(_current);
        }
    }

    internal void Reset()
    {
        Entry? previous;
        lock (_gate) { previous = _current; _current = null; }
        previous?.Release();
    }

    internal void Invalidate(Lease lease)
    {
        Entry? previous = null;
        lock (_gate)
        {
            if (ReferenceEquals(_current, lease.Entry))
            { previous = _current; _current = null; }
        }
        previous?.Release();
    }

    internal sealed class Entry(T value)
    {
        private int _references = 1; // The owner holds one reference between requests.
        internal T Value { get; } = value;
        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0) Value.Dispose();
        }
    }

    internal sealed class Lease(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;
        internal Entry? Entry => _entry;
        internal T Value => _entry?.Value ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
    }
}
