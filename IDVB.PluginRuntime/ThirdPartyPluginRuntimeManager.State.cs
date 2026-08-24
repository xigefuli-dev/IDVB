using System.Text.Json;
using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

public sealed partial class ThirdPartyPluginRuntimeManager
{
    private async Task RecoverStartupStateAsync(CancellationToken cancellationToken)
    {
        string? suspectedPluginId = null;
        if (File.Exists(_directories.LoadingMarkerPath))
        {
            var marker = await ReadJsonAsync<PluginLoadingMarker>(_directories.LoadingMarkerPath, cancellationToken);
            suspectedPluginId = marker?.PluginId;
            if (suspectedPluginId is not null)
            {
                await QuarantineAsync(
                    suspectedPluginId,
                    "IDVB previously exited while this plugin was initializing.",
                    cancellationToken);
            }
            TryDeleteFile(_directories.LoadingMarkerPath);
        }

        var crashState = await ReadJsonAsync<PluginCrashState>(_directories.CrashStatePath, cancellationToken) ?? new();
        var abnormal = File.Exists(_directories.SessionMarkerPath)
            ? crashState.ConsecutiveAbnormalExits + 1
            : 0;
        TryDeleteFile(_directories.SessionMarkerPath);
        await WriteJsonAsync(
            _directories.CrashStatePath,
            new PluginCrashState { ConsecutiveAbnormalExits = abnormal },
            cancellationToken);
        SafeMode = new PluginSafeModeState
        {
            IsActive = abnormal >= 2,
            ConsecutiveAbnormalExits = abnormal,
            SuspectedPluginId = suspectedPluginId
        };
    }

    private async Task EnsurePublisherStillTrustedAsync(
        PluginCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        if (_directories.DeveloperMode && entry.PublisherKeyId is null)
            return;
        var publishers = await _state.ReadPublishersAsync(cancellationToken);
        if (!publishers.Publishers.Any(publisher =>
                publisher.PublisherId == entry.PublisherId &&
                string.Equals(publisher.KeyId, entry.PublisherKeyId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The plugin publisher is no longer trusted.");
        }
    }

    private async Task QuarantineAsync(string pluginId, string reason, CancellationToken cancellationToken)
    {
        await _state.UpdateCatalogAsync(
            catalog => catalog with
            {
                Plugins = catalog.Plugins.Select(plugin => plugin.Id == pluginId
                    ? plugin with { Enabled = false, QuarantineReason = reason }
                    : plugin).ToArray()
            },
            cancellationToken);
    }

    private void PopulateInitialStatuses(PluginCatalog catalog)
    {
        lock (_statuses)
        {
            _statuses.Clear();
            foreach (var entry in catalog.Plugins)
            {
                var version = entry.PendingVersion ?? entry.ActiveVersion ?? string.Empty;
                var state = entry.QuarantineReason is not null
                    ? ThirdPartyPluginState.Quarantined
                    : entry.PendingVersion is not null
                        ? ThirdPartyPluginState.PendingRestart
                        : ThirdPartyPluginState.Disabled;
                _statuses[entry.Id] = new ThirdPartyPluginStatus
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName,
                    Version = version,
                    State = state,
                    Detail = entry.QuarantineReason
                };
            }
        }
    }

    private void SetStatus(
        PluginCatalogEntry entry,
        string version,
        ThirdPartyPluginState state,
        string? detail)
    {
        lock (_statuses)
        {
            _statuses[entry.Id] = new ThirdPartyPluginStatus
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Version = version,
                State = state,
                Detail = detail
            };
        }
    }

    private static IIdvbPlugin CreatePlugin(
        PluginLoadContext loadContext,
        string entryPath,
        string entryType)
    {
        var assembly = loadContext.LoadFromAssemblyPath(entryPath);
        var type = assembly.GetType(entryType, throwOnError: true, ignoreCase: false)!;
        if (type.IsAbstract || !typeof(IIdvbPlugin).IsAssignableFrom(type))
            throw new InvalidOperationException("The entry type does not implement IIdvbPlugin.");
        return (IIdvbPlugin)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("The plugin entry type could not be constructed."));
    }

    private static async Task InvokeWithTimeoutAsync(
        Func<CancellationToken, ValueTask> callback,
        TimeSpan timeout,
        CancellationToken pluginLifetime,
        CancellationToken hostCancellation)
    {
        using var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime, hostCancellation);
        var callbackTask = Task.Run(async () => await callback(callbackCancellation.Token), CancellationToken.None);
        try
        {
            await callbackTask.WaitAsync(timeout, hostCancellation);
        }
        catch (TimeoutException)
        {
            callbackCancellation.Cancel();
            throw;
        }
    }

    private static async Task<IdvpManifest> ReadManifestAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(packageDirectory, "manifest.json"));
        return await JsonSerializer.DeserializeAsync<IdvpManifest>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken)
            ?? throw new InvalidDataException("Installed plugin manifest is empty.");
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class LoadedPlugin(
        PluginCatalogEntry catalogEntry,
        IdvpManifest manifest,
        PluginLoadContext loadContext,
        CancellationTokenSource lifetime,
        IPluginContextLease contextLease,
        IIdvbPlugin plugin)
    {
        public PluginCatalogEntry CatalogEntry { get; } = catalogEntry;
        public IdvpManifest Manifest { get; } = manifest;
        public PluginLoadContext LoadContext { get; } = loadContext;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public IPluginContextLease ContextLease { get; } = contextLease;
        public IIdvbPlugin Plugin { get; } = plugin;
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
    }
}
