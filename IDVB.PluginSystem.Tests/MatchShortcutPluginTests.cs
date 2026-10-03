using System.Collections.Concurrent;
using System.Text.Json;
using IDVB.Sample.MatchShortcuts;
using IDVBuff.PluginContracts;
using IDVBuff.Features.Plugins.V2;
using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginRuntime;
using IdentityVisionBridge.PluginSdk;
using IdentityVisionBridge.Vision;

namespace IDVB.PluginSystem.Tests;

public sealed class MatchShortcutPluginTests
{
    [Fact]
    public async Task SignedIdvpLoadsBetweenMatchesSwitchesBothClassesAndReleasesItsBindingsWhenDisabled()
    {
        using var fixture = new PluginPackageTestFixture(typeof(MatchShortcutsPlugin));
        var manifest = JsonSerializer.Deserialize<IdvpManifest>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MatchShortcuts.manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var package = Environment.GetEnvironmentVariable("IDVB_MATCH_SHORTCUTS_PACKAGE")
            ?? await fixture.PackAsync(manifest);
        var validated = await new IdvpPackageReader().ValidateAsync(package, options: new() { ExtractFiles = false });
        Assert.True(validated.IsSigned);
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "appdata"), developerMode: false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.6.6");
        await installer.InstallAsync(package, new PluginInstallApproval
        {
            TrustPublisher = true, ApprovedCapabilities = manifest.Capabilities.ToHashSet(StringComparer.Ordinal)
        });
        await installer.ApplyStartupChangesAsync();
        await installer.SetEnabledAsync(manifest.Id, true);
        var host = new Host();
        var source = new Capabilities(host);
        var contexts = new DefaultThirdPartyPluginContextFactory(source, _ => new DelegatePluginLogger((_, _, _) => { }));
        await using var runtime = new ThirdPartyPluginRuntimeManager(directories, state, installer, contexts);
        host.Runtime = runtime;
        await runtime.SetMatchActivationAsync(false);
        await runtime.StartAsync();
        Assert.Contains(runtime.Statuses, item => item.State == ThirdPartyPluginState.Running);
        Assert.Equal(2, source.Input.Handlers.Count);
        Assert.True(PluginInputBinding.TryParse(source.Defaults["difficult"], out var difficult));
        Assert.Equal("Alt + F + C", difficult.DisplayName);
        Assert.True(PluginInputBinding.TryParse(source.Defaults["boss"], out var boss));
        Assert.Equal("Alt + F + B", boss.DisplayName);

        await source.Input.DispatchAsync("difficult", PluginInputTransition.Released);
        Assert.Empty(host.Requests);
        await source.Input.DispatchAsync("difficult", PluginInputTransition.Pressed).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await source.Input.DispatchAsync("boss", PluginInputTransition.Pressed).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { MatchShortcutsPlugin.DifficultClass, MatchShortcutsPlugin.BossClass }, host.Requests.Select(item => item.MapClass));
        Assert.All(host.Requests, request => Assert.True(request.EndCurrentMatch));
        Assert.Equal(2, host.EndTransitions);
        Assert.Equal(1, source.Created); // No stop/reload cycle when the plugin ends its own match.
        await runtime.SetMatchActivationAsync(false);
        Assert.Contains(runtime.Statuses, item => item.State == ThirdPartyPluginState.Running);
        await runtime.SetEnabledAsync(manifest.Id, false);
        Assert.Empty(source.Input.Handlers);
        Assert.DoesNotContain(runtime.Statuses, item => item.State == ThirdPartyPluginState.Running);
        await runtime.StopAsync();
        host.Runtime = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public async Task MatchShortcutPackageCannotActivateOnTheOlderSdk()
    {
        using var fixture = new PluginPackageTestFixture(typeof(MatchShortcutsPlugin));
        var manifest = JsonSerializer.Deserialize<IdvpManifest>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MatchShortcuts.manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var package = await fixture.PackAsync(manifest);
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "old-host"), false);
        var installer = new IdvpInstaller(directories, new PluginStateRepository(directories), "1.6.5", "2.0.0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(package, new()
        {
            TrustPublisher = true, ApprovedCapabilities = manifest.Capabilities.ToHashSet(StringComparer.Ordinal)
        }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RapidSwitchesKeepRunningWithTheProductionNotificationCapability(bool useCommands, bool recoverQuarantine)
    {
        using var fixture = new PluginPackageTestFixture(typeof(MatchShortcutsPlugin));
        var manifest = JsonSerializer.Deserialize<IdvpManifest>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MatchShortcuts.manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var package = Environment.GetEnvironmentVariable("IDVB_MATCH_SHORTCUTS_PACKAGE")
            ?? await fixture.PackAsync(manifest);
        var directories = new PluginDirectories(Path.Combine(fixture.Root, "notification-host"), false);
        var state = new PluginStateRepository(directories);
        var installer = new IdvpInstaller(directories, state, "1.6.6");
        await installer.InstallAsync(package, new()
        {
            TrustPublisher = true, ApprovedCapabilities = manifest.Capabilities.ToHashSet(StringComparer.Ordinal)
        });
        await installer.ApplyStartupChangesAsync();
        await installer.SetEnabledAsync(manifest.Id, true);
        if (recoverQuarantine)
            await state.UpdateCatalogAsync(catalog => catalog with
            {
                Plugins = catalog.Plugins.Select(entry => entry.Id == manifest.Id ? entry with
                {
                    Enabled = false,
                    QuarantineReason = "Plugin callback failed: Plugin notification rate limit exceeded."
                } : entry).ToArray()
            });
        try { await ExerciseRapidSwitchesAsync(directories, state, installer, manifest.Id, useCommands, recoverQuarantine); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task ExerciseRapidSwitchesAsync(PluginDirectories directories,
        PluginStateRepository state, IdvpInstaller installer, string pluginId, bool useCommands, bool recoverQuarantine)
    {
        var host = new Host();
        var input = new HostInput();
        var overlay = new RecordingOverlayNotificationService();
        var notifications = new PluginNotificationCenter(overlay);
        var delivered = new ConcurrentQueue<HostedPluginNotification>();
        notifications.NotificationPosted += (_, item) => delivered.Enqueue(item);
        var faults = new ConcurrentQueue<string>();
        var faultTasks = new ConcurrentQueue<Task>();
        var logs = new ConcurrentQueue<string>();
        ThirdPartyPluginRuntimeManager? runtime = null;
        void ReportFault(string id, Exception exception)
        {
            faults.Enqueue(exception.Message);
            faultTasks.Enqueue(runtime!.ReportFaultAsync(id, $"Plugin callback failed: {exception.Message}"));
        }
        var source = new ThirdPartyPluginCapabilitySource(new ThirdPartyHostEventHub(), input,
            new UnusedScreenshots(), notifications, ReportFault,
            new VisionCapabilityProvider(host, () => throw new InvalidOperationException("No engine needed.")));
        var contexts = new DefaultThirdPartyPluginContextFactory(source,
            _ => new DelegatePluginLogger((_, message, _) => logs.Enqueue(message)), ReportFault);
        await using var runtimeLease = runtime = new(directories, state, installer, contexts);
        host.Runtime = runtime;
        try
        {
            await runtime.SetMatchActivationAsync(false);
            await runtime.StartAsync();
            if (recoverQuarantine)
            {
                Assert.Empty(input.Bindings);
                Assert.Contains(runtime.Statuses, status => status.Id == pluginId && status.State == ThirdPartyPluginState.Quarantined);
                await runtime.RetryAsync(pluginId);
            }
            for (var index = 0; index < 12; index++)
            {
                var difficult = index % 2 == 0;
                if (useCommands)
                {
                    var result = await runtime.ExecuteCommandAsync(pluginId, difficult ? "start-difficult" : "start-boss");
                    Assert.True(result.Status == PluginCommandStatus.Success, result.Message);
                }
                else
                {
                    input.Raise(pluginId, difficult ? "difficult" : "boss");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (logs.Count(message => message.Contains(" · Applied · ", StringComparison.Ordinal)) < index + 1
                        && faults.IsEmpty)
                        await Task.Delay(10, timeout.Token);
                }
                Assert.Empty(faults);
            }
            Assert.Equal(12, host.EndTransitions);
            Assert.Equal(5, delivered.Count);
            Assert.Equal(5, overlay.Items.Count);
            Assert.All(overlay.Items, item => Assert.Equal(TimeSpan.FromSeconds(5), item.Duration));
            Assert.All(host.Requests.Select((request, index) => (request, index)), item =>
                Assert.Equal(item.index % 2 == 0 ? MatchShortcutsPlugin.DifficultClass : MatchShortcutsPlugin.BossClass,
                    item.request.MapClass));
            Assert.Contains(runtime.Statuses, status => status.Id == pluginId && status.State == ThirdPartyPluginState.Running);
            var entry = (await state.ReadCatalogAsync()).Plugins.Single(item => item.Id == pluginId);
            Assert.True(entry.Enabled);
            Assert.Null(entry.QuarantineReason);
            Assert.Equal(2, input.Bindings.Count);
            await runtime.SetEnabledAsync(pluginId, false);
            Assert.Empty(input.Bindings);
        }
        finally
        {
            await Task.WhenAll(faultTasks);
            await runtime.StopAsync();
            host.Runtime = null!;
        }
    }

    private sealed class HostInput : IPluginInputService
    {
        public event EventHandler<PluginInputEventArgs>? BindingInvoked;
        public readonly ConcurrentDictionary<(string Plugin, string Binding), PluginInputBinding> Bindings = new();
        public void SetBinding(string pluginId, string key, PluginInputBinding binding) => Bindings[(pluginId, key)] = binding;
        public void ClearBindings(string pluginId)
        {
            foreach (var key in Bindings.Keys.Where(key => key.Plugin == pluginId)) Bindings.TryRemove(key, out _);
        }
        public bool IsBindingPressed(string pluginId, string key) => false;
        public void Raise(string pluginId, string key) => BindingInvoked?.Invoke(this, new(pluginId, key, 0, true));
    }

    private sealed class UnusedScreenshots : IPluginScreenshotService
    {
        public Task<IDVBuff.PluginContracts.PluginScreenshotResult> CaptureAsync(TimeSpan? delay = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Capabilities(Host host) : IPluginCapabilitySource
    {
        public readonly Input Input = new();
        public readonly Dictionary<string, string> Defaults = new();
        public int Created;
        public ValueTask<IReadOnlyDictionary<Type, IPluginCapability>> CreateAsync(IdvpManifest manifest, string directory,
            PluginSettingsService settings, IReadOnlySet<string> granted, CancellationToken lifetime, CancellationToken token)
        {
            Created++;
            foreach (var key in new[] { "difficult", "boss" }) Defaults[key] = settings.Current.GetString(key)!;
            var caps = new Dictionary<Type, IPluginCapability> { [typeof(IInputBindingsCapability)] = Input };
            new VisionCapabilityProvider(host, () => throw new InvalidOperationException("No engine should be created.")).AddCapabilities(caps, granted, lifetime);
            return ValueTask.FromResult<IReadOnlyDictionary<Type, IPluginCapability>>(caps);
        }
    }
    private sealed class Input : IInputBindingsCapability
    {
        public readonly Dictionary<string, Func<PluginInputEvent, CancellationToken, ValueTask>> Handlers = new();
        public IDisposable Subscribe(string id, Func<PluginInputEvent, CancellationToken, ValueTask> handler)
        { Handlers.Add(id, handler); return new Subscription(() => Handlers.Remove(id)); }
        public ValueTask DispatchAsync(string id, PluginInputTransition transition) => Handlers[id](new()
            { BindingId = id, Transition = transition }, CancellationToken.None);
        private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
    private sealed class Host : IPluginVisionHost, IPluginControlHost
    {
        public ThirdPartyPluginRuntimeManager Runtime = null!;
        public readonly List<HostMatchRequest> Requests = [];
        public int EndTransitions;
        private readonly ConcurrentDictionary<string, HostOperationResult> _operations = new();
        public Task<HostOperationResult> QueueMatchAsync(HostMatchRequest? request, CancellationToken token)
        {
            Assert.NotNull(request);
            Requests.Add(request);
            var id = Guid.NewGuid().ToString("N");
            var result = _operations[id] = new(id, HostOperationState.Accepted, "queued");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Runtime.SetMatchActivationAsync(false);
                    EndTransitions++;
                    await Runtime.SetMatchActivationAsync(true);
                    _operations[id] = new(id, HostOperationState.Applied, request.MapClass);
                }
                catch (Exception exception) { _operations[id] = new(id, HostOperationState.Failed, exception.Message); }
            });
            return Task.FromResult(result);
        }
        public Task<HostOperationResult?> GetMatchOperationAsync(string id, CancellationToken token) => Task.FromResult(_operations.GetValueOrDefault(id));
        public Task<IReadOnlyList<string>> GetMapClassesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([MatchShortcutsPlugin.DifficultClass, MatchShortcutsPlugin.BossClass]);
        public Task<HostVisionSnapshot> GetSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<VisionScanResult> ScanAsync(HostScanRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<VisionAlignmentResult> AlignAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostOperationResult> SelectMapAsync(Guid id, string floor, CancellationToken token) => throw new NotSupportedException();
        public Task<HostOperationResult> SelectFloorAsync(string floor, CancellationToken token) => throw new NotSupportedException();
        public Task<HostOperationResult> SetOverlayVisibleAsync(bool visible, CancellationToken token) => throw new NotSupportedException();
        public Task<HostSettingsSnapshot> GetSettingsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostOperationResult> UpdateSettingsAsync(HostSettingsPatch patch, CancellationToken token) => throw new NotSupportedException();
    }
}
