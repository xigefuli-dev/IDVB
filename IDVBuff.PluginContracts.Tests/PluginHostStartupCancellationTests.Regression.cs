using System.Collections.Concurrent;
using IDVBuff.PluginContracts;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed partial class PluginHostStartupCancellationTests
{
    [Theory]
    [InlineData("load")]
    [InlineData("enable")]
    [InlineData("start")]
    public async Task LateCallbackFailureDuringCancellationRemainsVisibleAfterRollback(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new PluginHost(new MessageBus(), new FakeContextFactory());
        var first = new CallbackPlugin("first");
        var next = new CallbackPlugin("next");
        first.Callback = callbackStage =>
        {
            if (callbackStage != stage) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Callback was not released.");
            throw new InvalidOperationException("late plugin failure: " + stage);
        };
        host.Register(first);
        host.Register(next);
        var startup = Task.Run(() => host.Start(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            release.Set();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => startup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("late plugin failure: " + stage, failure.Message);
            Assert.Equal("unload", first.Calls.Last());
            Assert.False(host.IsActive(first.Id));
            Assert.Empty(next.Calls);
        }
        finally
        {
            release.Set();
            try { await startup.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
            first.Callback = null;
        }
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("unload")]
    public async Task CancelledLaterPluginRollsBackEarlierActivePluginsDespiteCleanupFailure(string failedCleanup)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeline = new ConcurrentQueue<string>();
        var factory = new FakeContextFactory();
        var bus = new MessageBus();
        using var host = new PluginHost(bus, factory);
        var first = new CallbackPlugin("first");
        var second = new CallbackPlugin("second");
        var third = new CallbackPlugin("third");
        first.Callback = stage =>
        {
            timeline.Enqueue("first:" + stage);
            if (stage == failedCleanup) throw new InvalidOperationException("controlled cleanup failure");
        };
        second.Callback = stage =>
        {
            timeline.Enqueue("second:" + stage);
            if (stage != "start") return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Startup callback was not released.");
        };
        host.Register(first);
        host.Register(second);
        host.Register(third);
        var startup = Task.Run(() => host.Start(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(host.IsActive(first.Id));
            cancellation.Cancel();
            Assert.False(startup.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(["second:disable", "second:unload", "first:disable", "first:unload"],
                timeline.Where(value => value.EndsWith(":disable") || value.EndsWith(":unload")));
            Assert.Empty(third.Calls);
            Assert.False(host.IsActive(first.Id));
            Assert.False(host.IsActive(second.Id));
            Assert.Equal(2, factory.Created.Count);
            Assert.Contains(factory.Logger.Errors, value => value.Contains("controlled cleanup failure"));
            host.Tick();
            bus.Publish("cancelled lifetime");
            Assert.Empty(first.Received);
            Assert.Empty(second.Received);

            var oldContexts = factory.Created.ToArray();
            first.Callback = null;
            second.Callback = null;
            first.Calls.Clear();
            second.Calls.Clear();
            host.Start();
            Assert.Equal(5, factory.Created.Count);
            Assert.NotSame(oldContexts[0], factory.Created[2]);
            Assert.NotSame(oldContexts[1], factory.Created[3]);
            Assert.Equal(["load", "enable", "start"], first.Calls);
            Assert.Equal(["load", "enable", "start"], second.Calls);
            Assert.True(host.IsActive(third.Id));
            bus.Publish("fresh lifetime");
            Assert.Equal(["fresh lifetime"], first.Received);
            Assert.Equal(["fresh lifetime"], second.Received);
            Assert.Equal(["fresh lifetime"], third.Received);
        }
        finally
        {
            release.Set();
            try { await startup.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            first.Callback = null;
            second.Callback = null;
        }
    }

    [Fact]
    public async Task CancelledSettingsRestoreKeepsEarlierDisabledPluginDisabledAfterRestart()
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeContextFactory();
        var bus = new MessageBus();
        using var host = new PluginHost(bus, factory);
        var disabled = new CallbackPlugin("disabled");
        var selected = new CallbackPlugin("selected");
        host.Register(disabled, initiallyEnabled: false);
        host.Register(selected);
        host.ContextInitialized = plugin =>
        {
            if (plugin.Id != selected.Id) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Settings restore was not released.");
        };
        var startup = Task.Run(() => host.Start(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(["load", "unload"], disabled.Calls);
            Assert.Equal(["load", "unload"], selected.Calls);
            Assert.False(host.IsEnabled(disabled.Id));
            Assert.True(host.IsEnabled(selected.Id));
            host.ContextInitialized = null;
            disabled.Calls.Clear();
            selected.Calls.Clear();
            host.Start();
            Assert.Equal(["load"], disabled.Calls);
            Assert.Equal(["load", "enable", "start"], selected.Calls);
            Assert.False(host.IsActive(disabled.Id));
            Assert.True(host.IsActive(selected.Id));
            Assert.Equal(4, factory.Created.Count);
            bus.Publish("fresh settings lifetime");
            Assert.Empty(disabled.Received);
            Assert.Equal(["fresh settings lifetime"], selected.Received);
        }
        finally
        {
            release.Set();
            try { await startup.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            host.ContextInitialized = null;
        }
    }
}
