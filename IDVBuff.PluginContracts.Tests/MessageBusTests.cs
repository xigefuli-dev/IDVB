using IDVBuff.PluginContracts;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public class MessageBusTests
{
    private sealed class RecordingHandler : IHandle<string>
    {
        public List<string> Received { get; } = new();

        public void Handle(string message) => Received.Add(message);
    }

    private sealed class ThrowingHandler : IHandle<string>
    {
        public void Handle(string message) => throw new InvalidOperationException("boom");
    }

    private sealed class MultiHandler : IHandle<string>, IHandle<int>
    {
        public void Handle(string message) { }

        public void Handle(int message) { }
    }

    private sealed class OpenAndObjectHandler : IHandle<object>
    {
        public void Handle(object message) { }
    }

    [Fact]
    public void Publish_DeliversToSubscribedHandler()
    {
        var bus = new MessageBus();
        var handler = new RecordingHandler();
        bus.Subscribe(handler);

        bus.Publish("hello");

        Assert.Equal(["hello"], handler.Received);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var bus = new MessageBus();
        var handler = new RecordingHandler();
        bus.Subscribe(handler);
        bus.Unsubscribe(handler);

        bus.Publish("hello");

        Assert.Empty(handler.Received);
    }

    [Fact]
    public void Publish_DeliversToAllHandlersOfSameType()
    {
        var bus = new MessageBus();
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        bus.Subscribe(first);
        bus.Subscribe(second);

        bus.Publish("hello");

        Assert.Equal(["hello"], first.Received);
        Assert.Equal(["hello"], second.Received);
    }

    [Fact]
    public void Publish_WithNoSubscribers_IsNoOp()
    {
        var bus = new MessageBus();

        bus.Publish("hello");
    }

    [Fact]
    public void Publish_IsolatesThrowingHandler()
    {
        var bus = new MessageBus();
        var good = new RecordingHandler();
        bus.Subscribe(new ThrowingHandler());
        bus.Subscribe(good);

        bus.Publish("hello");

        Assert.Equal(["hello"], good.Received);
    }

    [Fact]
    public void Subscribe_SameHandlerTwice_DoesNotDuplicate()
    {
        var bus = new MessageBus();
        var handler = new RecordingHandler();
        bus.Subscribe(handler);
        bus.Subscribe(handler);

        bus.Publish("hello");

        Assert.Single(handler.Received);
    }

    [Fact]
    public void GetHandlerMessageTypes_ReturnsClosedGenericHandles()
    {
        var types = MessageBus.GetHandlerMessageTypes(new MultiHandler());

        Assert.Contains(typeof(string), types);
        Assert.Contains(typeof(int), types);
    }

    [Fact]
    public void GetHandlerMessageTypes_IgnoresObjectHandle()
    {
        var types = MessageBus.GetHandlerMessageTypes(new OpenAndObjectHandler());

        Assert.Empty(types);
    }
}
