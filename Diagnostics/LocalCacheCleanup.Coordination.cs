namespace IDVBuff.Diagnostics;

internal static partial class LocalCacheCleanup
{
    private static readonly SemaphoreSlim WorkflowGate = new(1, 1);
    private static readonly object ExecutionGate = new();

    // Keep automatic retention out of the complete preview/confirmation workflow.
    // This never takes the recognition writer gate while waiting for the user.
    internal static async Task<IDisposable> BeginManualAsync()
    {
        await WorkflowGate.WaitAsync();
        return new WorkflowLease();
    }

    internal static IDisposable? TryBeginAutomatic() =>
        WorkflowGate.Wait(0) ? new WorkflowLease() : null;

    private sealed class WorkflowLease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) WorkflowGate.Release();
        }
    }
}
