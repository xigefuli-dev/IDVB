namespace IDVBuff.Features.Maps;

/// <summary>Serializes readers of resident Mats with cache publication and disposal.</summary>
internal sealed class MapCatalogResourceGate
{
    private readonly SemaphoreSlim _gate = new(1,1);
    public IDisposable Enter()
    {
        _gate.Wait();
        return new Lease(_gate);
    }
    public IDisposable? TryEnter(int timeoutMilliseconds,CancellationToken cancellationToken=default) =>
        _gate.Wait(timeoutMilliseconds,cancellationToken) ? new Lease(_gate) : null;
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken=default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate=gate;
        public void Dispose() => Interlocked.Exchange(ref _gate,null)?.Release();
    }
}
