using System.Collections.Concurrent;

namespace IDVBuff.Features.Maps;

/// <summary>Owns WGC creation and native shutdown on one MTA thread.</summary>
internal static class CaptureStreamWorker
{
    private static readonly Lazy<Worker> Owner = new(() => new Worker());

    internal static bool HasThreadAccess => Owner.IsValueCreated
        && Environment.CurrentManagedThreadId == Owner.Value.ThreadId;

    internal static Task RunAsync(Action action) => Owner.Value.Enqueue(action);

    private sealed class Worker
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly Thread _thread;

        internal Worker()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _work.GetConsumingEnumerable()) action();
            }) { IsBackground = true, Name = "IDVB WGC resources" };
            _thread.SetApartmentState(ApartmentState.MTA);
            // A long-lived capture owner must not inherit the first scan's
            // AsyncLocal deadline, frame or diagnostic request context.
            if (ExecutionContext.IsFlowSuppressed()) _thread.Start();
            else
            {
                using var flow = ExecutionContext.SuppressFlow();
                _thread.Start();
            }
        }

        internal int ThreadId => _thread.ManagedThreadId;

        internal Task Enqueue(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try { action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
            return completion.Task;
        }
    }
}
