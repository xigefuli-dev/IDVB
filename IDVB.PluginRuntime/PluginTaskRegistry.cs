using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

public sealed class PluginTaskRegistry : IPluginTaskRegistry, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly Action<Exception>? _faulted;
    private readonly object _sync = new();
    private readonly HashSet<ManagedPluginTask> _tasks = [];
    private bool _disposed;

    public PluginTaskRegistry(CancellationToken pluginLifetime, Action<Exception>? faulted = null)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime);
        _faulted = faulted;
    }

    public PluginTaskHandle Run(string name, Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = new ManagedPluginTask(name, operation, _lifetime.Token, Remove, _faulted);
            _tasks.Add(task);
            task.Start();
            return task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ManagedPluginTask[] tasks;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            tasks = _tasks.ToArray();
        }

        foreach (var task in tasks)
        {
            await task.DisposeAsync();
        }

        _lifetime.Dispose();
    }

    private void Remove(ManagedPluginTask task)
    {
        lock (_sync)
        {
            _tasks.Remove(task);
        }
    }

    private sealed class ManagedPluginTask : PluginTaskHandle
    {
        private readonly Func<CancellationToken, Task> _operation;
        private readonly CancellationTokenSource _cancellation;
        private readonly Action<ManagedPluginTask> _onCompleted;
        private readonly Action<Exception>? _faulted;
        private Task _completion = Task.CompletedTask;
        private int _started;

        public ManagedPluginTask(
            string name,
            Func<CancellationToken, Task> operation,
            CancellationToken lifetime,
            Action<ManagedPluginTask> onCompleted,
            Action<Exception>? faulted)
        {
            Name = name;
            _operation = operation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _onCompleted = onCompleted;
            _faulted = faulted;
        }

        public override string Name { get; }

        public override Task Completion => _completion;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            _completion = Task.Run(() => _operation(_cancellation.Token), CancellationToken.None);
            _ = _completion.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted && completed.Exception is { } exception)
                        _faulted?.Invoke(exception.GetBaseException());
                    _onCompleted(this);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public override async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            try
            {
                await _completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
