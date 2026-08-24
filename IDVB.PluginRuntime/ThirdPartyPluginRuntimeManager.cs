using System.Reflection;
using System.Text.Json;
using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

public sealed partial class ThirdPartyPluginRuntimeManager : IAsyncDisposable
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly PluginDirectories _directories;
    private readonly PluginStateRepository _state;
    private readonly IdvpInstaller _installer;
    private readonly IThirdPartyPluginContextFactory _contextFactory;
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ThirdPartyPluginStatus> _statuses = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _started;

    public ThirdPartyPluginRuntimeManager(
        PluginDirectories directories,
        PluginStateRepository state,
        IdvpInstaller installer,
        IThirdPartyPluginContextFactory contextFactory)
    {
        _directories = directories;
        _state = state;
        _installer = installer;
        _contextFactory = contextFactory;
    }

    public PluginSafeModeState SafeMode { get; private set; } = new();

    public IReadOnlyList<ThirdPartyPluginStatus> Statuses
    {
        get
        {
            lock (_statuses)
            {
                return _statuses.Values.OrderBy(status => status.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            _directories.EnsureCreated();
            await RecoverStartupStateAsync(cancellationToken);
            await _installer.ApplyStartupChangesAsync(cancellationToken);
            await _installer.RecheckCompatibilityAsync(cancellationToken);
            var catalog = await _state.ReadCatalogAsync(cancellationToken);
            PopulateInitialStatuses(catalog);
            var enabled = catalog.Plugins
                .Where(static plugin => plugin.Enabled && plugin.ActiveVersion is not null &&
                                        plugin.QuarantineReason is null && !plugin.CapabilityApprovalRequired)
                .ToArray();

            if (SafeMode.IsActive || enabled.Length == 0)
            {
                _started = true;
                return;
            }

            await WriteJsonAsync(
                _directories.SessionMarkerPath,
                new PluginSessionMarker { EnabledPluginIds = enabled.Select(static plugin => plugin.Id).ToArray() },
                cancellationToken);

            foreach (var entry in enabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryLoadAsync(entry, cancellationToken);
            }

            _started = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<PluginCommandResult> ExecuteCommandAsync(
        string pluginId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        if (!_loaded.TryGetValue(pluginId, out var loaded))
        {
            return PluginCommandResult.Failure("The plugin is not running.");
        }

        if (!loaded.Manifest.Commands.Any(command => command.Id == commandId))
        {
            return PluginCommandResult.Failure("The command is not declared by the plugin manifest.");
        }

        if (loaded.Plugin is not IPluginCommandHandler handler)
        {
            return PluginCommandResult.Failure("The plugin does not implement command handling.");
        }

        if (!await loaded.CommandGate.WaitAsync(0, cancellationToken))
        {
            return PluginCommandResult.Failure("The plugin is already executing a command.");
        }

        try
        {
            return await Task.Run(
                async () => await handler.ExecuteAsync(commandId, cancellationToken),
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PluginCommandResult.Cancelled();
        }
        catch (Exception exception)
        {
            await QuarantineAsync(pluginId, $"Command {commandId} failed: {exception.GetBaseException().Message}", CancellationToken.None);
            return PluginCommandResult.Failure("The plugin command failed and the plugin was quarantined.");
        }
        finally
        {
            loaded.CommandGate.Release();
        }
    }

    public async Task SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await _installer.SetEnabledAsync(pluginId, enabled, cancellationToken);
            if (!enabled)
            {
                if (_loaded.Remove(pluginId, out var loaded))
                    await StopOneAsync(loaded, cancellationToken);
                return;
            }

            if (SafeMode.IsActive || _loaded.ContainsKey(pluginId))
                return;
            var catalog = await _state.ReadCatalogAsync(cancellationToken);
            var entry = catalog.Plugins.SingleOrDefault(plugin => plugin.Id == pluginId);
            if (entry is { Enabled: true, ActiveVersion: not null, PendingVersion: null,
                    QuarantineReason: null, CapabilityApprovalRequired: false })
            {
                await TryLoadAsync(entry, cancellationToken);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RetryAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await _installer.ClearQuarantineAsync(pluginId, cancellationToken);
        await SetEnabledAsync(pluginId, true, cancellationToken);
    }

    public async Task DisableAllAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await _installer.DisableAllAsync(cancellationToken);
            foreach (var loaded in _loaded.Values.Reverse().ToArray())
                await StopOneAsync(loaded, cancellationToken);
            _loaded.Clear();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ReportFaultAsync(
        string pluginId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            PluginCatalogEntry? entry = null;
            if (_loaded.Remove(pluginId, out var loaded))
            {
                entry = loaded.CatalogEntry;
                await StopOneAsync(loaded, cancellationToken);
            }

            await QuarantineAsync(pluginId, reason, cancellationToken);
            entry ??= (await _state.ReadCatalogAsync(cancellationToken)).Plugins
                .SingleOrDefault(plugin => plugin.Id == pluginId);
            if (entry is not null)
                SetStatus(entry, entry.ActiveVersion ?? entry.PendingVersion ?? string.Empty,
                    ThirdPartyPluginState.Quarantined, reason);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ScheduleRollbackAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await _installer.ScheduleRollbackAsync(pluginId, cancellationToken);
            if (_loaded.Remove(pluginId, out var loaded))
                await StopOneAsync(loaded, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<(IdvpManifest Manifest, PluginSettingsService Settings)> GetSettingsAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        if (_loaded.TryGetValue(pluginId, out var loaded) &&
            loaded.ContextLease.Context.Settings is PluginSettingsService activeSettings)
        {
            return (loaded.Manifest, activeSettings);
        }

        var catalog = await _state.ReadCatalogAsync(cancellationToken);
        var entry = catalog.Plugins.SingleOrDefault(plugin => plugin.Id == pluginId)
            ?? throw new InvalidOperationException("Plugin is not installed.");
        var version = entry.PendingVersion ?? entry.ActiveVersion
            ?? throw new InvalidOperationException("Plugin has no installed version.");
        var manifest = await ReadManifestAsync(
            _directories.GetPackageDirectory(pluginId, version), cancellationToken);
        var dataDirectory = _directories.GetDataDirectory(entry.PublisherId, entry.Id);
        var settings = await PluginSettingsService.CreateAsync(dataDirectory, manifest.Settings, cancellationToken);
        return (manifest, settings);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var loaded in _loaded.Values.Reverse().ToArray())
            {
                await StopOneAsync(loaded, cancellationToken);
            }

            _loaded.Clear();
            TryDeleteFile(_directories.LoadingMarkerPath);
            TryDeleteFile(_directories.SessionMarkerPath);
            await WriteJsonAsync(_directories.CrashStatePath, new PluginCrashState(), cancellationToken);
            _started = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task TryLoadAsync(PluginCatalogEntry entry, CancellationToken cancellationToken)
    {
        var version = entry.ActiveVersion!;
        SetStatus(entry, version, ThirdPartyPluginState.Starting, null);
        await WriteJsonAsync(
            _directories.LoadingMarkerPath,
            new PluginLoadingMarker { PluginId = entry.Id, Version = version },
            cancellationToken);

        LoadedPlugin? loaded = null;
        try
        {
            var packageDirectory = _directories.GetPackageDirectory(entry.Id, version);
            var manifest = await ReadManifestAsync(packageDirectory, cancellationToken);
            await EnsurePublisherStillTrustedAsync(entry, cancellationToken);
            var requested = manifest.Capabilities.ToHashSet(StringComparer.Ordinal);
            if (!requested.IsSubsetOf(entry.GrantedCapabilities))
            {
                throw new InvalidOperationException("The plugin requests capabilities that are not approved.");
            }

            var entryPath = Path.GetFullPath(Path.Combine(
                packageDirectory,
                manifest.EntryPoint.Assembly.Replace('/', Path.DirectorySeparatorChar)));
            var loadContext = new PluginLoadContext(entryPath);
            var lifetime = new CancellationTokenSource();
            var dataDirectory = _directories.GetDataDirectory(entry.PublisherId, entry.Id);
            Directory.CreateDirectory(dataDirectory);
            var contextLease = await _contextFactory.CreateAsync(
                manifest, dataDirectory, requested, lifetime.Token, cancellationToken);

            var plugin = await Task.Run(() => CreatePlugin(loadContext, entryPath, manifest.EntryPoint.Type), cancellationToken);
            loaded = new LoadedPlugin(entry, manifest, loadContext, lifetime, contextLease, plugin);
            await InvokeWithTimeoutAsync(
                token => plugin.InitializeAsync(contextLease.Context, token),
                InitializeTimeout,
                lifetime.Token,
                cancellationToken);
            await InvokeWithTimeoutAsync(
                plugin.StartAsync,
                StartTimeout,
                lifetime.Token,
                cancellationToken);
            _loaded.Add(entry.Id, loaded);
            SetStatus(entry, version, ThirdPartyPluginState.Running, null);
        }
        catch (Exception exception)
        {
            if (loaded is not null)
            {
                await AbandonAsync(loaded);
            }

            var reason = exception is TimeoutException
                ? "Plugin lifecycle callback timed out."
                : exception.GetBaseException().Message;
            await QuarantineAsync(entry.Id, reason, CancellationToken.None);
            SetStatus(entry, version, ThirdPartyPluginState.Quarantined, reason);
        }
        finally
        {
            TryDeleteFile(_directories.LoadingMarkerPath);
        }
    }

    private async Task StopOneAsync(LoadedPlugin loaded, CancellationToken cancellationToken)
    {
        SetStatus(loaded.CatalogEntry, loaded.Manifest.Version, ThirdPartyPluginState.Stopping, null);
        loaded.Lifetime.Cancel();
        try
        {
            await loaded.ContextLease.RevokeAsync(cancellationToken);
            await InvokeWithTimeoutAsync(
                loaded.Plugin.StopAsync,
                StopTimeout,
                CancellationToken.None,
                cancellationToken);
        }
        catch
        {
            // Shutdown failures remain isolated to this plugin.
        }

        try
        {
            await InvokeWithTimeoutAsync(
                _ => loaded.Plugin.DisposeAsync(),
                DisposeTimeout,
                CancellationToken.None,
                cancellationToken);
        }
        catch
        {
        }

        await loaded.ContextLease.DisposeAsync();
        loaded.Lifetime.Dispose();
        loaded.CommandGate.Dispose();
        loaded.LoadContext.Unload();
        SetStatus(loaded.CatalogEntry, loaded.Manifest.Version, ThirdPartyPluginState.Disabled, null);
    }

    private static async Task AbandonAsync(LoadedPlugin loaded)
    {
        loaded.Lifetime.Cancel();
        try
        {
            await loaded.ContextLease.RevokeAsync(CancellationToken.None);
        }
        catch
        {
        }

        try
        {
            await loaded.Plugin.DisposeAsync().AsTask().WaitAsync(DisposeTimeout);
        }
        catch
        {
        }

        await loaded.ContextLease.DisposeAsync();
        loaded.Lifetime.Dispose();
        loaded.CommandGate.Dispose();
        loaded.LoadContext.Unload();
    }

}
