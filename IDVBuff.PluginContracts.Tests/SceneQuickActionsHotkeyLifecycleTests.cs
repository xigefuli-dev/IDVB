using System.Diagnostics;
using System.Reflection;
using IDVBuff.PluginContracts;
using IDVBuff.Plugins.SceneQuickActions;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class SceneQuickActionsHotkeyLifecycleTests
{
    private const uint F5 = 0x74;
    private const uint F6 = 0x75;
    private const uint G = 0x47;
    private const uint H = 0x48;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void EnableDisableEnableDeliversBothDefaultHotkeysOnTheSameRunner()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner;
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F5)));
        AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));
        fixture.Host.SetEnabled(fixture.Plugin.Id, false);
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        Assert.Same(runner, fixture.Runner);
        AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F5)));
        AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));
    }

    [Fact]
    public void LoadedDisabledPluginDoesNotSubscribeOrRegisterOnRefresh()
    {
        using var fixture = new Fixture();
        fixture.Plugin.RefreshHotkeys();
        Assert.Equal(0, fixture.Input.SubscriberCount);
        Assert.Empty(fixture.Input.Bindings);
        AssertIgnored(fixture.Runner, () => Invoke(fixture.Runner, "OnHotkeyInvoked",
            fixture.Input, Event(fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey)));
    }

    [Fact]
    public void RepeatedEnableDisableRestoresBothDefaultsWithoutDuplicateSubscriptions()
    {
        using var fixture = new Fixture();
        var runner = fixture.Runner;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            fixture.Host.SetEnabled(fixture.Plugin.Id, true);
            fixture.Host.SetEnabled(fixture.Plugin.Id, true);
            Invoke(runner, "Start");
            Invoke(runner, "AttachHotkeys", fixture.Input, fixture.Plugin.Id);
            fixture.Plugin.RefreshHotkeys();
            Assert.Same(runner, fixture.Runner);
            Assert.Equal(1, fixture.Input.SubscriberCount);
            Assert.Equal(2, fixture.Input.Bindings.Count);
            Assert.Equal(PluginInputBinding.Keyboard(F5), fixture.Input.Binding(fixture.Plugin.Id,
                SceneQuickActionsOptions.DropBagHotkeyKey));
            Assert.Equal(PluginInputBinding.Keyboard(F6), fixture.Input.Binding(fixture.Plugin.Id,
                SceneQuickActionsOptions.DropHotbarHotkeyKey));
            AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F5)));
            AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));

            var queued = fixture.Input.SnapshotHandlers();
            var oldEvent = Event(fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey);
            fixture.Host.SetEnabled(fixture.Plugin.Id, false);
            fixture.Host.SetEnabled(fixture.Plugin.Id, false);
            Invoke(runner, "Stop");
            fixture.Plugin.RefreshHotkeys();
            Assert.Equal(0, fixture.Input.SubscriberCount);
            Assert.Empty(fixture.Input.Bindings);
            AssertIgnored(runner, () => queued?.Invoke(fixture.Input, oldEvent));
            AssertIgnored(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F5)));
            fixture.Host.SetEnabled(fixture.Plugin.Id, true);
            AssertIgnored(runner, () => fixture.Input.Deliver(oldEvent));
            AssertDelivered(runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));
            fixture.Host.SetEnabled(fixture.Plugin.Id, false);
        }
    }

    [Fact]
    public void ChangingKeyboardAndMouseBindingsReplacesOldKeysAndSurvivesRestart()
    {
        using var fixture = new Fixture();
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        var oldEvent = Event(fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey);
        var sideButton = PluginInputBinding.Mouse(PluginMouseButton.XButton1);
        fixture.Plugin.SetSettingValue(SceneQuickActionsOptions.DropBagHotkeyKey,
            PluginInputBinding.Keyboard(G).StorageValue);
        fixture.Plugin.SetSettingValue(SceneQuickActionsOptions.DropHotbarHotkeyKey, sideButton.StorageValue);
        Assert.Equal(1, fixture.Input.SubscriberCount);
        AssertIgnored(fixture.Runner, () => fixture.Input.Deliver(oldEvent));
        AssertIgnored(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F5)));
        AssertIgnored(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(G)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(sideButton));

        fixture.Plugin.SetSettingValue(SceneQuickActionsOptions.DropBagHotkeyKey,
            PluginInputBinding.Keyboard(H).StorageValue);
        AssertIgnored(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(G)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(H)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(sideButton));

        fixture.Host.SetEnabled(fixture.Plugin.Id, false);
        var otherSideButton = PluginInputBinding.Mouse(PluginMouseButton.XButton2);
        fixture.Plugin.SetSettingValue(SceneQuickActionsOptions.DropBagHotkeyKey,
            otherSideButton.StorageValue);
        fixture.Plugin.SetSettingValue(SceneQuickActionsOptions.DropHotbarHotkeyKey,
            PluginInputBinding.Keyboard(G).StorageValue);
        fixture.Plugin.RefreshHotkeys();
        Assert.Empty(fixture.Input.Bindings);
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        Assert.Equal(otherSideButton, fixture.Input.Binding(fixture.Plugin.Id,
            SceneQuickActionsOptions.DropBagHotkeyKey));
        Assert.Equal(PluginInputBinding.Keyboard(G), fixture.Input.Binding(fixture.Plugin.Id,
            SceneQuickActionsOptions.DropHotbarHotkeyKey));
        AssertIgnored(fixture.Runner, () => fixture.Input.Press(sideButton));
        AssertIgnored(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(H)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(otherSideButton));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(G)));
    }

    [Fact]
    public void ReplacingOrRemovingInputServiceClearsOnlyTheOldPluginBindings()
    {
        using var fixture = new Fixture();
        fixture.Input.SetBinding("other-plugin", "key", PluginInputBinding.Keyboard(F5));
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        var oldHandlers = fixture.Input.SnapshotHandlers();
        var replacement = new RecordingInput();
        Invoke(fixture.Runner, "AttachHotkeys", replacement, fixture.Plugin.Id);
        Assert.Equal(0, fixture.Input.SubscriberCount);
        Assert.Equal("other-plugin", Assert.Single(fixture.Input.Bindings).Key.PluginId);
        Assert.Equal(1, replacement.SubscriberCount);
        Assert.Equal(2, replacement.Bindings.Count);
        AssertIgnored(fixture.Runner, () => oldHandlers?.Invoke(fixture.Input,
            Event(fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey)));
        AssertDelivered(fixture.Runner, () => replacement.Press(PluginInputBinding.Keyboard(F5)));

        Invoke(fixture.Runner, "AttachHotkeys", null, fixture.Plugin.Id);
        Assert.Equal(0, replacement.SubscriberCount);
        Assert.Empty(replacement.Bindings);
        fixture.Plugin.RefreshHotkeys();
        fixture.Host.SetEnabled(fixture.Plugin.Id, false);
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        Assert.Empty(replacement.Bindings);
        Invoke(fixture.Runner, "AttachHotkeys", replacement, fixture.Plugin.Id);
        Assert.Equal(1, replacement.SubscriberCount);
        AssertDelivered(fixture.Runner, () => replacement.Press(PluginInputBinding.Keyboard(F6)));
        fixture.Host.SetEnabled(fixture.Plugin.Id, false);
        Assert.Equal(0, replacement.SubscriberCount);
        Assert.Empty(replacement.Bindings);
    }

    [Fact]
    public void HostUnloadDisposesRunnerAndReloadCreatesFreshBindings()
    {
        using var fixture = new Fixture();
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        var runner = fixture.Runner;
        var queued = fixture.Input.SnapshotHandlers();
        fixture.Host.Stop();
        fixture.Host.Stop();
        Assert.Equal(0, fixture.Input.SubscriberCount);
        Assert.Empty(fixture.Input.Bindings);
        Assert.Null(Field(runner, "_hotkeyService").GetValue(runner));
        AssertIgnored(runner, () => queued?.Invoke(fixture.Input,
            Event(fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey)));
        Invoke(runner, "Dispose");
        Invoke(runner, "RefreshHotkeyBindings");
        Assert.IsType<ObjectDisposedException>(Assert.Throws<TargetInvocationException>(() =>
            Invoke(runner, "Start")).InnerException);
        Assert.IsType<ObjectDisposedException>(Assert.Throws<TargetInvocationException>(() =>
            Invoke(runner, "AttachHotkeys", fixture.Input, fixture.Plugin.Id)).InnerException);
        Assert.Empty(fixture.Input.Bindings);

        fixture.Host.Start();
        Assert.NotSame(runner, fixture.Runner);
        Assert.Equal(1, fixture.Input.SubscriberCount);
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F5)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));
    }

    [Fact]
    public void RefreshRacingWithStopCannotRestoreDisabledBindings()
    {
        using var fixture = new Fixture();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            fixture.Host.SetEnabled(fixture.Plugin.Id, true);
            Parallel.Invoke(() => Invoke(fixture.Runner, "Stop"), fixture.Plugin.RefreshHotkeys);
            fixture.Host.SetEnabled(fixture.Plugin.Id, false);
            fixture.Plugin.RefreshHotkeys();
            Assert.Equal(0, fixture.Input.SubscriberCount);
            Assert.Empty(fixture.Input.Bindings);
        }
    }

    [Fact]
    public void ReleasesAndUnrelatedPluginEventsDoNotEnterTheActionHandler()
    {
        using var fixture = new Fixture();
        fixture.Host.SetEnabled(fixture.Plugin.Id, true);
        AssertIgnored(fixture.Runner, () => fixture.Input.Deliver(new PluginInputEventArgs(
            fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey, Stopwatch.GetTimestamp(), false)));
        AssertIgnored(fixture.Runner, () => fixture.Input.Deliver(
            Event("other-plugin", SceneQuickActionsOptions.DropBagHotkeyKey)));
        AssertIgnored(fixture.Runner, () => fixture.Input.Deliver(Event(fixture.Plugin.Id, "other-key")));
        Invoke(fixture.Runner, "SetOptions", new SceneQuickActionsOptions
        {
            InviteEnabled = false,
            PickupEnabled = false,
            DropBagHotkey = new PluginInputBinding()
        });
        fixture.Plugin.RefreshHotkeys();
        Assert.False(fixture.Input.Binding(fixture.Plugin.Id,
            SceneQuickActionsOptions.DropBagHotkeyKey).IsConfigured);
        AssertIgnored(fixture.Runner, () => fixture.Input.Deliver(
            Event(fixture.Plugin.Id, SceneQuickActionsOptions.DropBagHotkeyKey)));
        AssertDelivered(fixture.Runner, () => fixture.Input.Press(PluginInputBinding.Keyboard(F6)));
    }

    private static PluginInputEventArgs Event(string pluginId, string key) =>
        new(pluginId, key, Stopwatch.GetTimestamp(), true);

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, PrivateInstance)!;

    private static void Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Public | PrivateInstance)!.Invoke(target, args);

    private static void AssertDelivered(object runner, Action dispatch) => AssertDispatch(runner, dispatch, true);
    private static void AssertIgnored(object runner, Action dispatch) => AssertDispatch(runner, dispatch, false);

    private static void AssertDispatch(object runner, Action dispatch, bool expected)
    {
        // Stop at the existing busy gate, before foreground inspection or native input.
        // The debounce timestamp shows whether the actual callback accepted this event.
        Field(runner, "_hotkeyBusy").SetValue(runner, 1);
        Field(runner, "_lastHotkeyAt").SetValue(runner, DateTime.MinValue);
        try
        {
            dispatch();
            Assert.Equal(expected, (DateTime)Field(runner, "_lastHotkeyAt").GetValue(runner)! != DateTime.MinValue);
        }
        finally
        {
            Field(runner, "_hotkeyBusy").SetValue(runner, 0);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public RecordingInput Input { get; } = new();
        public SceneQuickActionsPlugin Plugin { get; } = new();
        public PluginHost Host { get; }
        public object Runner => Field(Plugin, "_runner").GetValue(Plugin)
            ?? throw new InvalidOperationException("The real plugin OnLoad did not create a runner.");

        public Fixture()
        {
            Plugin.SetSettingValue(SceneQuickActionsOptions.InviteEnabledKey, false);
            Plugin.SetSettingValue(SceneQuickActionsOptions.PickupEnabledKey, false);
            var factory = new FakeContextFactory(Input);
            Host = new PluginHost(new MessageBus(), factory);
            Host.Register(Plugin, initiallyEnabled: false);
            Host.Start();
            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(Runner);
        }

        public void Dispose() => Host.Dispose();
    }

    private sealed class RecordingInput : IPluginInputService, IPluginGameWindowService
    {
        private EventHandler<PluginInputEventArgs>? _handlers;
        public Dictionary<(string PluginId, string Key), PluginInputBinding> Bindings { get; } = new();
        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        public event EventHandler<PluginInputEventArgs>? BindingInvoked
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public void SetBinding(string pluginId, string bindingKey, PluginInputBinding binding) =>
            Bindings[(pluginId, bindingKey)] = binding.Clone();

        public void ClearBindings(string pluginId)
        {
            foreach (var key in Bindings.Keys.Where(key => key.PluginId == pluginId).ToArray())
                Bindings.Remove(key);
        }

        public bool IsBindingPressed(string pluginId, string bindingKey) => false;
        public PluginInputBinding Binding(string pluginId, string key) => Bindings[(pluginId, key)];
        public EventHandler<PluginInputEventArgs>? SnapshotHandlers() => _handlers;
        public void Deliver(PluginInputEventArgs args) => _handlers?.Invoke(this, args);

        public void Press(PluginInputBinding binding)
        {
            foreach (var (key, configured) in Bindings.ToArray())
                if (configured.IsConfigured && configured.Equals(binding))
                    Deliver(Event(key.PluginId, key.Key));
        }

        public bool TryGetForegroundClientBounds(out PluginClientBounds bounds, out IntPtr window, out string failure)
        {
            bounds = default;
            window = IntPtr.Zero;
            failure = "No game window is used by these lifecycle tests.";
            return false;
        }
    }
}
