using IDVBuff.PluginContracts;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "11")]
public sealed partial class PluginHostStartupCancellationTests
{
    [Fact]
    public void AlreadyCancelledStartupDoesNotCreateContextsOrInvokePlugins()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new FakeContextFactory();
        using var host = new PluginHost(new MessageBus(), factory);
        var plugin = new CallbackPlugin("first");
        host.Register(plugin);

        Assert.ThrowsAny<OperationCanceledException>(() => host.Start(cancellation.Token));

        Assert.Empty(plugin.Calls);
        Assert.Empty(factory.Created);
        Assert.False(host.IsActive(plugin.Id));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("enable")]
    [InlineData("start")]
    public async Task CancellationDuringACallbackWaitsForItAndRollsBackBeforeStartingTheNextPlugin(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var factory = new FakeContextFactory();
        var bus = new MessageBus();
        using var host = new PluginHost(bus, factory);
        host.SetActivationAllowed(false);
        var first = new CallbackPlugin("first");
        var next = new CallbackPlugin("next");
        first.Callback = callbackStage =>
        {
            if (callbackStage != stage) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the startup callback.");
        };
        host.Register(first);
        host.Register(next);
        var startup = Task.Run(() => host.Start(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            // The owner must drain this task before disposing its dependencies.
            Assert.False(startup.IsCompleted);
            Assert.DoesNotContain("unload", first.Calls);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await startup.WaitAsync(TimeSpan.FromSeconds(5)));

            var expected = stage switch
            {
                "load" => new[] { "load", "unload" },
                "enable" => ["load", "enable", "disable", "unload"],
                _ => ["load", "enable", "start", "disable", "unload"]
            };
            Assert.Equal(expected, first.Calls);
            Assert.Empty(next.Calls);
            Assert.Single(factory.Created);
            Assert.False(host.IsActive(first.Id));
            Assert.False(host.IsActive(next.Id));
            host.Tick();
            bus.Publish("after cancellation");
            Assert.Equal(expected, first.Calls);
            Assert.Empty(first.Received);

            // A cancelled initialization must not leave disposed adapters behind.
            first.Callback = null;
            first.Calls.Clear();
            host.Start();
            Assert.Equal(["load", "enable", "start"], first.Calls);
            Assert.True(host.IsActive(first.Id));
            Assert.True(host.IsActive(next.Id));
            Assert.Equal(3, factory.Created.Count);
            bus.Publish("after restart");
            Assert.Equal(["after restart"], first.Received);
        }
        finally
        {
            release.Set();
            try { await startup.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void CancellationWhileRestoringSettingsDoesNotEnableThePlugin()
    {
        using var cancellation = new CancellationTokenSource();
        using var host = new PluginHost(new MessageBus(), new FakeContextFactory());
        var plugin = new CallbackPlugin("first");
        host.Register(plugin);
        host.ContextInitialized = _ => cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => host.Start(cancellation.Token));

        Assert.Equal(["load", "unload"], plugin.Calls);
        Assert.False(host.IsActive(plugin.Id));
    }

    [Plugin("startup-callback", AlwaysActive = true)]
    private sealed class CallbackPlugin(string id) : PluginBase, IHandle<string>
    {
        public override string Id => id;
        public override string DisplayName => id;
        public List<string> Calls { get; } = [];
        public List<string> Received { get; } = [];
        public Action<string>? Callback { get; set; }
        public override void OnLoad(IPluginContext context)
        {
            base.OnLoad(context);
            Record("load");
        }
        public override void OnEnable() => Record("enable");
        public override void OnStart() => Record("start");
        public override void OnTick() => Record("tick");
        public override void OnDisable() => Record("disable");
        public override void OnUnload() => Record("unload");
        public void Handle(string message) => Received.Add(message);
        private void Record(string stage)
        {
            Calls.Add(stage);
            Callback?.Invoke(stage);
        }
    }
}
