using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Features.Plugins.V2;

public sealed class ThirdPartyHostEventBridge :
    IHandle<MatchStateChangedMessage>,
    IHandle<SessionStateChangedMessage>,
    IHandle<MapLockedMessage>,
    IHandle<SurveyStatusChangedMessage>,
    IHandle<ConfigChangedMessage>,
    IHandle<ResolutionChangedMessage>,
    IDisposable
{
    private readonly IMessageBus _bus;
    private readonly ThirdPartyHostEventHub _hub;
    private bool _attached;

    public ThirdPartyHostEventBridge(IMessageBus bus, ThirdPartyHostEventHub hub)
    {
        _bus = bus;
        _hub = hub;
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _bus.Subscribe<MatchStateChangedMessage>(this);
        _bus.Subscribe<SessionStateChangedMessage>(this);
        _bus.Subscribe<MapLockedMessage>(this);
        _bus.Subscribe<SurveyStatusChangedMessage>(this);
        _bus.Subscribe<ConfigChangedMessage>(this);
        _bus.Subscribe<ResolutionChangedMessage>(this);
    }

    public void Handle(MatchStateChangedMessage message) =>
        _hub.Publish(new MatchStateChangedEvent { State = message.State, Mode = message.Mode });

    public void Handle(SessionStateChangedMessage message)
    {
        _hub.Publish(new SessionStateChangedEvent
        {
            State = message.SessionState,
            SessionId = message.MapId,
            Extensions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["floor"] = message.Floor ?? string.Empty,
                ["locationMethod"] = message.LocationMethod
            }
        });
        _hub.Publish(new MapLockChangedEvent
        {
            IsLocked = message.IsLocked,
            MapId = message.MapId,
            FloorId = message.Floor,
            HasTrustedAlignment = message.IsLocked && message.AlignmentRevision > 0
        });
    }

    public void Handle(MapLockedMessage message) =>
        _hub.Publish(new MapLockChangedEvent
        {
            IsLocked = true,
            MapId = message.MapId,
            FloorId = message.Floor,
            HasTrustedAlignment = true
        });

    public void Handle(SurveyStatusChangedMessage message) =>
        _hub.Publish(new SurveyStateChangedEvent
        {
            State = message.RuntimeState,
            Detail = message.LastMessage
        });

    public void Handle(ConfigChangedMessage message) => PublishConfiguration("configuration");

    public void Handle(ResolutionChangedMessage message) => PublishConfiguration("resolution");

    public void Dispose()
    {
        if (!_attached) return;
        _attached = false;
        _bus.Unsubscribe<MatchStateChangedMessage>(this);
        _bus.Unsubscribe<SessionStateChangedMessage>(this);
        _bus.Unsubscribe<MapLockedMessage>(this);
        _bus.Unsubscribe<SurveyStatusChangedMessage>(this);
        _bus.Unsubscribe<ConfigChangedMessage>(this);
        _bus.Unsubscribe<ResolutionChangedMessage>(this);
    }

    private void PublishConfiguration(string section) =>
        _hub.Publish(new HostConfigurationChangedEvent { ChangedSections = [section] });
}
