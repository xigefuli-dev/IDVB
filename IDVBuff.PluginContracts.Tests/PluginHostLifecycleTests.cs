using IDVBuff.PluginContracts;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public class PluginHostLifecycleTests
{
    private sealed class MessagePlugin : PluginBase, IHandle<string>
    {
        public override string Id => "msg";

        public List<string> Received { get; } = new();

        public void Handle(string message) => Received.Add(message);
    }

    private static PluginHost CreateHost(FakeContextFactory factory) =>
        new(new MessageBus(), factory);

    [Fact]
    public void Start_LoadsEnablesStartsEachPluginOnceInOrder()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var plugin = new RecordingPlugin("a");
        host.Register(plugin);

        host.Start();

        Assert.Equal(["load", "enable", "start"], plugin.Calls);
        Assert.Single(factory.Created);
        Assert.Equal("a", factory.Created[0].PluginId);
    }

    [Fact]
    public void Start_RespectsInitialDisabledState_AndCanEnableLater()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var plugin = new RecordingPlugin("a");
        host.Register(plugin, initiallyEnabled: false);

        host.Start();

        Assert.False(host.IsEnabled("a"));
        Assert.Equal(["load"], plugin.Calls);

        host.SetEnabled("a", enabled: true);

        Assert.True(host.IsEnabled("a"));
        Assert.Equal(["load", "enable", "start"], plugin.Calls);
    }

    [Fact]
    public void StopAndStart_PreservesTheCurrentDesiredState()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var plugin = new RecordingPlugin("a");
        host.Register(plugin);
        host.Start();

        host.SetEnabled("a", enabled: false);
        host.Stop();
        host.Start();

        Assert.False(host.IsEnabled("a"));
        Assert.Equal(
            ["load", "enable", "start", "disable", "unload", "load"],
            plugin.Calls);
    }

    [Fact]
    public void Tick_CallsOnTickForAllStartedPlugins()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var a = new RecordingPlugin("a");
        var b = new RecordingPlugin("b");
        host.Register(a);
        host.Register(b);
        host.Start();
        a.Calls.Clear();
        b.Calls.Clear();

        host.Tick();

        Assert.Equal(["tick"], a.Calls);
        Assert.Equal(["tick"], b.Calls);
    }

    [Fact]
    public void Tick_IsolatesThrowingPlugin()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var bad = new RecordingPlugin("bad")
        {
            OnTickAction = () => throw new InvalidOperationException("boom")
        };
        var good = new RecordingPlugin("good");
        host.Register(bad);
        host.Register(good);
        host.Start();
        bad.Calls.Clear();
        good.Calls.Clear();

        host.Tick();

        Assert.Equal(["tick"], bad.Calls);
        Assert.Equal(["tick"], good.Calls);
        Assert.Single(factory.Logger.Errors);
    }

    [Fact]
    public void Stop_DisablesUnloadsInReverseRegistrationOrder()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var global = new List<string>();
        host.Register(new RecordingPlugin("a", global));
        host.Register(new RecordingPlugin("b", global));
        host.Start();
        global.Clear();

        host.Stop();

        Assert.Equal(["b:disable", "b:unload", "a:disable", "a:unload"], global);
    }

    [Fact]
    public void StartStop_AreIdempotent()
    {
        var factory = new FakeContextFactory();
        var host = CreateHost(factory);
        var plugin = new RecordingPlugin("a");
        host.Register(plugin);

        host.Start();
        host.Start();
        Assert.Equal(["load", "enable", "start"], plugin.Calls);

        host.Stop();
        host.Stop();
        Assert.Equal(["load", "enable", "start", "disable", "unload"], plugin.Calls);
    }

    [Fact]
    public void Register_DuplicateId_CaseInsensitive_Throws()
    {
        var host = CreateHost(new FakeContextFactory());
        host.Register(new RecordingPlugin("a"));

        Assert.Throws<InvalidOperationException>(() => host.Register(new RecordingPlugin("A")));
    }

    [Fact]
    public void Register_BlankId_Throws()
    {
        var host = CreateHost(new FakeContextFactory());

        Assert.Throws<InvalidOperationException>(() => host.Register(new RecordingPlugin("")));
    }

    [Fact]
    public void Register_AfterStart_Throws()
    {
        var host = CreateHost(new FakeContextFactory());
        host.Register(new RecordingPlugin("a"));
        host.Start();

        Assert.Throws<InvalidOperationException>(() => host.Register(new RecordingPlugin("b")));
    }

    [Fact]
    public void PluginReceivesMessagesOnlyAfterStart()
    {
        var bus = new MessageBus();
        var factory = new FakeContextFactory();
        var host = new PluginHost(bus, factory);
        var plugin = new MessagePlugin();
        host.Register(plugin);

        bus.Publish("before");
        host.Start();
        bus.Publish("after1");
        host.Stop();
        bus.Publish("after-stop");

        Assert.Equal(["after1"], plugin.Received);
    }

    [Fact]
    public void SetEnabled_DisabledPluginDoesNotReceiveMessagesOrTicks_AndCanResume()
    {
        var bus = new MessageBus();
        var factory = new FakeContextFactory();
        var host = new PluginHost(bus, factory);
        var plugin = new MessagePlugin();
        host.Register(plugin);
        host.Start();

        Assert.True(host.IsEnabled("msg"));
        host.SetEnabled("msg", false);
        Assert.False(host.IsEnabled("msg"));

        bus.Publish("while-disabled");
        host.Tick();
        Assert.Empty(plugin.Received);

        host.SetEnabled("msg", true);
        Assert.True(host.IsEnabled("msg"));
        bus.Publish("after-reenable");

        Assert.Equal(["after-reenable"], plugin.Received);
    }

    [Fact]
    public void GetRequired_Missing_Throws()
    {
        var host = CreateHost(new FakeContextFactory());

        Assert.Throws<KeyNotFoundException>(() => host.GetRequired("nope"));
    }

    [Fact]
    public void TryGet_ReturnsRegistered()
    {
        var host = CreateHost(new FakeContextFactory());
        host.Register(new RecordingPlugin("a"));

        Assert.True(host.TryGet("a", out var plugin));
        Assert.NotNull(plugin);
        Assert.Equal("a", plugin!.Id);

        Assert.False(host.TryGet("zzz", out _));
    }

    [Fact]
    public void Plugins_ExposesRegisteredPlugins()
    {
        var host = CreateHost(new FakeContextFactory());
        host.Register(new RecordingPlugin("a"));
        host.Register(new RecordingPlugin("b"));

        Assert.Equal(2, host.Plugins.Count);
    }
}
