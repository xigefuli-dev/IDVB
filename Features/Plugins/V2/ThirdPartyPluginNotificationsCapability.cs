using IdentityVisionBridge.PluginSdk;
using IdentityVisionBridge.PluginRuntime;

namespace IDVBuff.Features.Plugins.V2;

internal sealed class ThirdPartyPluginNotificationsCapability(
    string pluginId,
    PluginNotificationCenter center,
    CancellationToken pluginLifetime,
    TimeProvider? timeProvider = null,
    Action<string, Exception>? reportFault = null) : IPluginNotificationsCapability, IRevocablePluginCapability
{
    private readonly object _sync = new();
    private readonly Queue<long> _recent = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime);
    private readonly HashSet<PendingDelivery> _pending = [];
    private bool _revoked;

    public ValueTask PostAsync(PluginNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        pluginLifetime.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(notification.Title) || notification.Title.Length > 128 ||
            string.IsNullOrWhiteSpace(notification.Message) || notification.Message.Length > 1000)
            throw new ArgumentException("Plugin notification text is empty or too long.", nameof(notification));
        if (!Enum.IsDefined(notification.Severity))
            throw new ArgumentOutOfRangeException(nameof(notification), "Unknown notification severity.");
        if (notification.Delay < TimeSpan.Zero || notification.Delay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(notification), "Notification delay is outside the supported timer range.");
        if (notification.Duration <= TimeSpan.Zero ||
            notification.Duration > DateTimeOffset.MaxValue - DateTimeOffset.UtcNow - notification.Delay)
            throw new ArgumentOutOfRangeException(nameof(notification), "Notification duration must be positive and finite.");

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_revoked, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (notification.Delay > TimeSpan.Zero)
            {
                // Scheduling must not hold an input callback or command open until it times out.
                // Bound scheduled work as well as displayed notifications.
                if (_pending.Count >= 5) return ValueTask.CompletedTask;
                var pending = new PendingDelivery(CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token, cancellationToken));
                _pending.Add(pending);
                pending.Task = DeliverLaterAsync(notification, pending);
            }
            else if (ReserveDelivery()) center.Post(pluginId, notification);
        }
        return ValueTask.CompletedTask;
    }

    private bool ReserveDelivery()
    {
        var now = _clock.GetTimestamp();
        while (_recent.TryPeek(out var postedAt) && _clock.GetElapsedTime(postedAt, now) >= TimeSpan.FromMinutes(1))
            _recent.Dequeue();
        // Excess notifications do not fault the plugin or extend its window.
        if (_recent.Count >= 5) return false;
        _recent.Enqueue(now);
        return true;
    }

    private async Task DeliverLaterAsync(PluginNotification notification, PendingDelivery pending)
    {
        try
        {
            await Task.Delay(notification.Delay, _clock, pending.Cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                pending.Cancellation.Token.ThrowIfCancellationRequested();
                if (!_revoked && ReserveDelivery()) center.Post(pluginId, notification);
            }
        }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (reportFault is null) throw;
            reportFault(pluginId, exception);
        }
        finally
        {
            lock (_sync) _pending.Remove(pending);
            pending.Cancellation.Dispose();
        }
    }

    public async ValueTask RevokeAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_sync)
        {
            if (_revoked) return;
            _revoked = true;
            tasks = _pending.Select(item => item.Task).ToArray();
        }
        _lifetime.Cancel();
        try { await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }

    private sealed class PendingDelivery(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
