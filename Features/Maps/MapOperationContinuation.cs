namespace IDVBuff.Features.Maps;

/// <summary>Preparation may finish late; only its current operation may commit state.</summary>
internal static class MapOperationContinuation
{
    internal static async Task<T> CommitAfterAsync<T>(Func<Task> prepare, Func<T> commit,
        CancellationToken cancellationToken, Func<bool>? isCurrent = null)
    {
        void CheckCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isCurrent?.Invoke() == false)
                throw new OperationCanceledException("Map operation was superseded.", cancellationToken);
        }

        CheckCurrent();
        await prepare();
        CheckCurrent();
        // The caller owns the UI transaction. No await may separate this check
        // from its capture-context, coverage and adaptive-controller writes.
        return commit();
    }
}
