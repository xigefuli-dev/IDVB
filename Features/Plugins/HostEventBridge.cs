using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 订阅宿主事件，将快照经 <see cref="HostMessageMapper"/> 转译为不可变 DTO 后发布到
/// <see cref="IMessageBus"/>。v1 采用同步直发（消息为不可变 DTO，跨线程安全）。
/// </summary>
public sealed class HostEventBridge : IDisposable
{
    private readonly IMessageBus _bus;
    private readonly SessionOrchestrator _session;
    private readonly IGlobalInput _input;
    private readonly ISurveyStatusSource _survey;
    private readonly IConfigProvider _config;
    private readonly IResolutionProfileService _profiles;
    private long _lastPublishedLockedRevision;

    public HostEventBridge(
        IMessageBus bus,
        SessionOrchestrator session,
        IGlobalInput input,
        ISurveyStatusSource survey,
        IConfigProvider config,
        IResolutionProfileService profiles)
    {
        _bus = bus;
        _session = session;
        _input = input;
        _survey = survey;
        _config = config;
        _profiles = profiles;
    }

    public void Attach()
    {
        _session.StateChanged += OnSessionStateChanged;
        _session.ElevationRequiredDetected += OnElevationRequiredDetected;

        _input.QuickScanInvoked += OnQuickScanInvoked;
        _input.OverlayToggleInvoked += OnOverlayToggleInvoked;
        _input.ManualRecognitionInvoked += OnManualRecognitionInvoked;
        _input.GameMapToggleInvoked += OnGameMapToggleInvoked;
        _input.ControlPanelToggleInvoked += OnControlPanelToggleInvoked;
        _input.SwitchFloorInvoked += OnSwitchFloorInvoked;
        _input.SaveMapCacheInvoked += OnSaveMapCacheInvoked;
        _input.RestMapDisplayInvoked += OnRestMapDisplayInvoked;
        _input.AltInvoked += OnAltInvoked;

        _survey.StatusChanged += OnSurveyStatusChanged;
        _config.ConfigChanged += OnConfigChanged;
        _profiles.ResolutionChanged += OnResolutionChanged;
    }

    public void Dispose()
    {
        _session.StateChanged -= OnSessionStateChanged;
        _session.ElevationRequiredDetected -= OnElevationRequiredDetected;

        _input.QuickScanInvoked -= OnQuickScanInvoked;
        _input.OverlayToggleInvoked -= OnOverlayToggleInvoked;
        _input.ManualRecognitionInvoked -= OnManualRecognitionInvoked;
        _input.GameMapToggleInvoked -= OnGameMapToggleInvoked;
        _input.ControlPanelToggleInvoked -= OnControlPanelToggleInvoked;
        _input.SwitchFloorInvoked -= OnSwitchFloorInvoked;
        _input.SaveMapCacheInvoked -= OnSaveMapCacheInvoked;
        _input.RestMapDisplayInvoked -= OnRestMapDisplayInvoked;
        _input.AltInvoked -= OnAltInvoked;

        _survey.StatusChanged -= OnSurveyStatusChanged;
        _config.ConfigChanged -= OnConfigChanged;
        _profiles.ResolutionChanged -= OnResolutionChanged;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        _bus.Publish(HostMessageMapper.ToSessionStateChanged(
            _session.SessionSnapshot,
            _session.MatchSnapshot,
            _session.StatusMessage,
            _session.IsOverlayVisible,
            _session.IsGameMapOpen,
            _session.AlignmentTrackingMode));
        _bus.Publish(HostMessageMapper.ToMatchStateChanged(_session.MatchSnapshot));

        var locked = HostMessageMapper.TryToMapLocked(
            _session.SessionSnapshot,
            _session.SessionSnapshot.LockedTransform,
            ref _lastPublishedLockedRevision);
        if (locked is not null)
            _bus.Publish(locked);
    }

    private void OnElevationRequiredDetected(object? sender, EventArgs e) =>
        _bus.Publish(new ElevationRequiredMessage());

    private void OnSurveyStatusChanged(object? sender, SurveyStatusSnapshot snapshot) =>
        _bus.Publish(HostMessageMapper.ToSurveyStatusChanged(snapshot));

    private void OnConfigChanged(object? sender, EventArgs e) =>
        _bus.Publish(new ConfigChangedMessage());

    private void OnResolutionChanged(object? sender, EventArgs e) =>
        _bus.Publish(new ResolutionChangedMessage(_config.ActiveResolutionPreset));

    private void OnQuickScanInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.QuickScan);

    private void OnOverlayToggleInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.OverlayToggle);

    private void OnManualRecognitionInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.ManualRecognition);

    private void OnGameMapToggleInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.GameMapToggle);

    private void OnControlPanelToggleInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.ControlPanelToggle);

    private void OnSwitchFloorInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.SwitchFloor);

    private void OnSaveMapCacheInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.SaveMapCache);

    private void OnRestMapDisplayInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.RestMapDisplay);

    private void OnAltInvoked(object? sender, object e) =>
        PublishHotkey(PluginHotkeyKind.Alt);

    private void PublishHotkey(PluginHotkeyKind kind) =>
        _bus.Publish(HostMessageMapper.ToHotkeyInvoked(kind));
}
