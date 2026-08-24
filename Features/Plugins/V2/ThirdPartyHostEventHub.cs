using System.Threading.Channels;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Features.Plugins.V2;

public sealed class ThirdPartyHostEventHub
{
    private readonly object _sync = new();
    private readonly HashSet<HostEventsCapability> _capabilities = [];

    public IHostEventsCapability CreateCapability(
        string pluginId,
        CancellationToken pluginLifetime,
        Action<string, Exception>? reportFault = null)
    {
        HostEventsCapability? capability = null;
        capability = new HostEventsCapability(
            pluginId,
            pluginLifetime,
            () => Remove(capability!),
            reportFault);
        lock (_sync)
            _capabilities.Add(capability);
        return capability;
    }

    public void Publish(PluginHostEvent hostEvent)
    {
        HostEventsCapability[] snapshot;
        lock (_sync)
            snapshot = _capabilities.ToArray();
        foreach (var capability in snapshot)
            capability.Publish(hostEvent);
    }

    private void Remove(HostEventsCapability capability)
    {
        lock (_sync)
            _capabilities.Remove(capability);
    }

    private sealed class HostEventsCapability :
        IHostEventsCapability,
        IRevocablePluginCapability
    {
        private readonly object _sync = new();
        private readonly Dictionary<Type, List<object>> _handlers = [];
        private readonly Dictionary<Type, PluginHostEvent> _latest = [];
        private readonly HashSet<Type> _signaled = [];
        private readonly Channel<Type> _signals = Channel.CreateBounded<Type>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly CancellationTokenSource _lifetime;
        private readonly Action _onRevoked;
        private readonly string _pluginId;
        private readonly Action<string, Exception>? _reportFault;
        private readonly Task _worker;
        private int _revoked;

        public HostEventsCapability(
            string pluginId,
            CancellationToken pluginLifetime,
            Action onRevoked,
            Action<string, Exception>? reportFault)
        {
            _pluginId = pluginId;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime);
            _onRevoked = onRevoked;
            _reportFault = reportFault;
            _worker = Task.Run(DispatchAsync);
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : PluginHostEvent
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _revoked) != 0, this);
                if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
                {
                    handlers = [];
                    _handlers.Add(typeof(TEvent), handlers);
                }

                handlers.Add(handler);
            }

            return new Subscription(() =>
            {
                lock (_sync)
                {
                    if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
                        handlers.Remove(handler);
                }
            });
        }

        public void Publish(PluginHostEvent hostEvent)
        {
            lock (_sync)
            {
                if (_revoked != 0)
                    return;
                var eventType = hostEvent.GetType();
                _latest[eventType] = hostEvent;
                if (_signaled.Add(eventType))
                    _signals.Writer.TryWrite(eventType);
            }
        }

        public async ValueTask RevokeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _revoked, 1) != 0)
                return;
            lock (_sync)
            {
                _handlers.Clear();
                _latest.Clear();
                _signaled.Clear();
            }
            _onRevoked();
            _lifetime.Cancel();
            _signals.Writer.TryComplete();
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        private async Task DispatchAsync()
        {
            try
            {
                await foreach (var eventType in _signals.Reader.ReadAllAsync(_lifetime.Token))
                {
                    PluginHostEvent? hostEvent;
                    object[] handlers;
                    lock (_sync)
                    {
                        _latest.Remove(eventType, out hostEvent);
                        _signaled.Remove(eventType);
                        handlers = _handlers.TryGetValue(eventType, out var registered)
                            ? registered.ToArray()
                            : [];
                    }

                    if (hostEvent is null)
                        continue;
                    foreach (var handler in handlers)
                    {
                        try
                        {
                            await InvokeAsync(handler, hostEvent, _lifetime.Token);
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            _reportFault?.Invoke(_pluginId, exception);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private static ValueTask InvokeAsync(object handler, PluginHostEvent hostEvent, CancellationToken token)
        {
            var method = handler.GetType().GetMethod("Invoke")
                ?? throw new InvalidOperationException("Plugin event handler has no Invoke method.");
            return (ValueTask)(method.Invoke(handler, [hostEvent, token])
                ?? throw new InvalidOperationException("Plugin event handler returned no task."));
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
