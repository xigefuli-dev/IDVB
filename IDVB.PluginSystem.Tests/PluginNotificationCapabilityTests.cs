using IDVBuff.Core.Contracts;
using System.Collections.Concurrent;
using IDVBuff.Features.Notifications;
using IDVBuff.Features.Plugins.V2;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;

namespace IDVB.PluginSystem.Tests;

public sealed class PluginNotificationCapabilityTests
{
    private static readonly PluginNotification Message = new() { Title = "对局快捷切换", Message = "对局已开始" };

    [Fact]
    public async Task BurstIsSuppressedWithoutThrowingOrGrowingTheNotificationQueue()
    {
        var center = new PluginNotificationCenter();
        var received = new List<HostedPluginNotification>();
        center.NotificationPosted += (_, item) => received.Add(item);
        var capability = new ThirdPartyPluginNotificationsCapability("test.plugin", center, default, new Clock());
        for (var index = 0; index < 100; index++) await capability.PostAsync(Message, default);
        Assert.Equal(5, received.Count);
        Assert.All(received, item => Assert.Equal("test.plugin", item.PluginId));
    }

    [Fact]
    public async Task RollingWindowRecoversAtItsBoundaryAndSuppressionDoesNotExtendIt()
    {
        var clock = new Clock();
        var center = new PluginNotificationCenter();
        var delivered = 0;
        center.NotificationPosted += (_, _) => delivered++;
        var capability = new ThirdPartyPluginNotificationsCapability("test.plugin", center, default, clock);
        for (var index = 0; index < 3; index++) await capability.PostAsync(Message, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 2; index++) await capability.PostAsync(Message, default);
        clock.Advance(TimeSpan.FromSeconds(29));
        await capability.PostAsync(Message, default);
        Assert.Equal(5, delivered);
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var index = 0; index < 4; index++) await capability.PostAsync(Message, default);
        Assert.Equal(8, delivered); // Only the three notifications from t=0 have expired.
        clock.Advance(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 3; index++) await capability.PostAsync(Message, default);
        Assert.Equal(10, delivered);
    }

    [Fact]
    public async Task ConcurrentPostsRespectTheSameLimit()
    {
        var center = new PluginNotificationCenter();
        var delivered = 0;
        center.NotificationPosted += (_, _) => Interlocked.Increment(ref delivered);
        var capability = new ThirdPartyPluginNotificationsCapability("test.plugin", center, default, new Clock());
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () => await capability.PostAsync(Message, default))));
        Assert.Equal(5, delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStillStopsNotificationDelivery(bool cancelLifetime)
    {
        using var cancelled = new CancellationTokenSource();
        var center = new PluginNotificationCenter();
        var delivered = 0;
        center.NotificationPosted += (_, _) => delivered++;
        var capability = new ThirdPartyPluginNotificationsCapability("test.plugin", center,
            cancelLifetime ? cancelled.Token : default);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            capability.PostAsync(Message, cancelLifetime ? default : cancelled.Token).AsTask());
        Assert.Equal(0, delivered);
    }

    [Theory]
    [InlineData("", "valid")]
    [InlineData("valid", " ")]
    [InlineData("long-title", "valid")]
    [InlineData("valid", "long-message")]
    public async Task InvalidTextIsStillRejected(string title, string message)
    {
        var capability = new ThirdPartyPluginNotificationsCapability("test.plugin", new(), default);
        var notification = new PluginNotification
        {
            Title = title == "long-title" ? new string('a', 129) : title,
            Message = message == "long-message" ? new string('a', 1001) : message
        };
        await Assert.ThrowsAsync<ArgumentException>(() => capability.PostAsync(notification, default).AsTask());
    }

    [Fact]
    public async Task PluginsHaveIndependentBudgets()
    {
        var center = new PluginNotificationCenter();
        var received = new List<HostedPluginNotification>();
        center.NotificationPosted += (_, item) => received.Add(item);
        foreach (var id in new[] { "first.plugin", "second.plugin" })
        {
            var capability = new ThirdPartyPluginNotificationsCapability(id, center, default, new Clock());
            for (var index = 0; index < 6; index++) await capability.PostAsync(Message, default);
        }
        Assert.Equal(10, received.Count);
        Assert.Equal(5, received.Count(item => item.PluginId == "first.plugin"));
        Assert.Equal(5, received.Count(item => item.PluginId == "second.plugin"));
    }

    [Theory]
    [InlineData(PluginNotificationSeverity.Error, OverlayNotificationType.Error)]
    [InlineData(PluginNotificationSeverity.Warning, OverlayNotificationType.Warning)]
    [InlineData(PluginNotificationSeverity.Success, OverlayNotificationType.Notice)]
    [InlineData(PluginNotificationSeverity.Information, OverlayNotificationType.Notice)]
    public async Task NotifyUsesTheRegisteredOverlayWithTheRequestedColorContentAndDuration(
        PluginNotificationSeverity severity, OverlayNotificationType type)
    {
        var overlay = new RecordingOverlayNotificationService();
        IPluginNotificationsCapability capability = new ThirdPartyPluginNotificationsCapability(
            "test.plugin", new(overlay), default);
        await capability.NotifyAsync(Message with { Severity = severity, Duration = TimeSpan.FromSeconds(8) });
        var item = Assert.Single(overlay.Items);
        Assert.Equal(type, item.Type);
        Assert.Equal("对局快捷切换\n对局已开始", item.Message);
        Assert.Equal(TimeSpan.FromSeconds(8), item.Duration);
    }

    [Fact]
    public async Task DelayControlsPostingTimeAndDisplayDurationStartsAtPosting()
    {
        var clock = new Clock();
        var overlay = new RecordingOverlayNotificationService();
        IPluginNotificationsCapability capability = new ThirdPartyPluginNotificationsCapability(
            "test.plugin", new(overlay), default, clock);
        await capability.NotifyAsync(Message with
            { Delay = TimeSpan.FromSeconds(30), Duration = TimeSpan.FromSeconds(8) });
        Assert.Empty(overlay.Items);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(overlay.Items);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (overlay.Items.Count == 0) await Task.Delay(10, timeout.Token);
        Assert.Equal(TimeSpan.FromSeconds(8), Assert.Single(overlay.Items).Duration);
        await ((IRevocablePluginCapability)capability).RevokeAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedNotificationsAreCancelledWithoutDeliveryOrConsumingABudgetSlot(bool cancelLifetime)
    {
        using var cancelled = new CancellationTokenSource();
        var clock = new Clock();
        var overlay = new RecordingOverlayNotificationService();
        IPluginNotificationsCapability capability = new ThirdPartyPluginNotificationsCapability(
            "test.plugin", new(overlay), cancelLifetime ? cancelled.Token : default, clock);
        await capability.NotifyAsync(Message with { Delay = TimeSpan.FromSeconds(30) },
            cancelLifetime ? default : cancelled.Token);
        cancelled.Cancel();
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(overlay.Items);
        if (!cancelLifetime)
        {
            for (var index = 0; index < 6; index++) await capability.NotifyAsync(Message);
            Assert.Equal(5, overlay.Items.Count);
        }
        await ((IRevocablePluginCapability)capability).RevokeAsync(default);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(5, -1)]
    [InlineData(5, 4294967295)]
    public async Task InvalidTimeControlsAreRejectedBeforePosting(long durationMs, long delayMs)
    {
        var overlay = new RecordingOverlayNotificationService();
        IPluginNotificationsCapability capability = new ThirdPartyPluginNotificationsCapability("test.plugin", new(overlay), default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => capability.NotifyAsync(Message with
            { Duration = TimeSpan.FromMilliseconds(durationMs), Delay = TimeSpan.FromMilliseconds(delayMs) }).AsTask());
        Assert.Empty(overlay.Items);
    }

    [Fact]
    public async Task DelayedWorkIsBoundedReturnsImmediatelyAndCannotPostAfterRevocation()
    {
        var clock = new Clock();
        var overlay = new RecordingOverlayNotificationService();
        IPluginNotificationsCapability capability = new ThirdPartyPluginNotificationsCapability("test.plugin", new(overlay), default, clock);
        for (var index = 0; index < 100; index++)
        {
            var scheduling = capability.NotifyAsync(Message with { Delay = TimeSpan.FromMinutes(10) });
            Assert.True(scheduling.IsCompletedSuccessfully);
            await scheduling;
        }
        Assert.Equal(5, clock.TimerCount);
        await ((IRevocablePluginCapability)capability).RevokeAsync(default);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(overlay.Items);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capability.NotifyAsync(Message).AsTask());
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly List<Timer> _timers = [];
        public int TimerCount => _timers.Count;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private long _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                return Volatile.Read(ref _disposed) == 0;
            }
            public void FireIfDue()
            {
                if (clock.GetTimestamp() >= _due && Interlocked.Exchange(ref _disposed, 1) == 0) callback(state);
            }
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

internal sealed class RecordingOverlayNotificationService : IOverlayNotificationService
{
    public readonly ConcurrentQueue<IOverlayNotification> Items = new();
    public event EventHandler<OverlayNotificationEventArgs>? NotificationPosted;
    public event Action<string>? DismissRequested;
    public IOverlayNotification Post(OverlayNotificationType type, string message, double? progress = null, TimeSpan? duration = null)
    {
        var item = new OverlayNotificationItem(type, message, progress, duration);
        Items.Enqueue(item);
        NotificationPosted?.Invoke(this, new(item));
        return item;
    }
    public IOverlayNotification Error(string message, double? progress = null, TimeSpan? duration = null) => Post(OverlayNotificationType.Error, message, progress, duration);
    public IOverlayNotification Warning(string message, double? progress = null, TimeSpan? duration = null) => Post(OverlayNotificationType.Warning, message, progress, duration);
    public IOverlayNotification Notice(string message, double? progress = null, TimeSpan? duration = null) => Post(OverlayNotificationType.Notice, message, progress, duration);
    public void Dismiss(string id) => Items.Single(item => item.Id == id).Dismiss();
    public void RequestDismiss(string id) { Dismiss(id); DismissRequested?.Invoke(id); }
    public void Clear() { foreach (var item in Items) item.Dismiss(); }
}
