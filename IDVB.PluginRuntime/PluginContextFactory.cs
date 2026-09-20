using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

public interface IThirdPartyPluginContextFactory
{
    ValueTask<IPluginContextLease> CreateAsync(
        IdvpManifest manifest,
        string dataDirectory,
        IReadOnlySet<string> grantedCapabilities,
        CancellationToken pluginLifetime,
        CancellationToken cancellationToken);
}

public interface IPluginContextLease : IAsyncDisposable
{
    IIdvbPluginContext Context { get; }

    ValueTask RevokeAsync(CancellationToken cancellationToken);
}

public interface IPluginCapabilitySource
{
    ValueTask<IReadOnlyDictionary<Type, IPluginCapability>> CreateAsync(
        IdvpManifest manifest,
        string dataDirectory,
        PluginSettingsService settings,
        IReadOnlySet<string> grantedCapabilities,
        CancellationToken pluginLifetime,
        CancellationToken cancellationToken);
}

public interface IRevocablePluginCapability
{
    ValueTask RevokeAsync(CancellationToken cancellationToken);
}

public sealed class DefaultThirdPartyPluginContextFactory : IThirdPartyPluginContextFactory
{
    private readonly IPluginCapabilitySource _capabilitySource;
    private readonly Func<IdvpManifest, IPluginLogger> _loggerFactory;
    private readonly Action<string, Exception>? _reportFault;

    public DefaultThirdPartyPluginContextFactory(
        IPluginCapabilitySource capabilitySource,
        Func<IdvpManifest, IPluginLogger> loggerFactory,
        Action<string, Exception>? reportFault = null)
    {
        _capabilitySource = capabilitySource;
        _loggerFactory = loggerFactory;
        _reportFault = reportFault;
    }

    public async ValueTask<IPluginContextLease> CreateAsync(
        IdvpManifest manifest,
        string dataDirectory,
        IReadOnlySet<string> grantedCapabilities,
        CancellationToken pluginLifetime,
        CancellationToken cancellationToken)
    {
        var settings = await PluginSettingsService.CreateAsync(
            dataDirectory,
            manifest.Settings,
            cancellationToken,
            exception => _reportFault?.Invoke(manifest.Id, exception));
        var tasks = new PluginTaskRegistry(
            pluginLifetime,
            exception => _reportFault?.Invoke(manifest.Id, exception));
        var capabilities = await _capabilitySource.CreateAsync(
            manifest, dataDirectory, settings, grantedCapabilities, pluginLifetime, cancellationToken);
        var context = new DefaultPluginContext(
            new PluginIdentity
            {
                Id = manifest.Id,
                DisplayName = manifest.DisplayName,
                Version = manifest.Version,
                PublisherId = manifest.Publisher.Id
            },
            _loggerFactory(manifest),
            settings,
            tasks,
            capabilities);
        return new DefaultPluginContextLease(context, tasks, capabilities.Values);
    }

    private sealed class DefaultPluginContext : IIdvbPluginContext
    {
        private readonly IReadOnlyDictionary<Type, IPluginCapability> _capabilities;

        public DefaultPluginContext(
            PluginIdentity identity,
            IPluginLogger logger,
            IPluginSettings settings,
            IPluginTaskRegistry tasks,
            IReadOnlyDictionary<Type, IPluginCapability> capabilities)
        {
            Identity = identity;
            Logger = logger;
            Settings = settings;
            Tasks = tasks;
            _capabilities = capabilities;
        }

        public PluginIdentity Identity { get; }

        public IPluginLogger Logger { get; }

        public IPluginSettings Settings { get; }

        public IPluginTaskRegistry Tasks { get; }

        public bool TryGetCapability<TCapability>(out TCapability? capability)
            where TCapability : class, IPluginCapability
        {
            if (_capabilities.TryGetValue(typeof(TCapability), out var candidate) && candidate is TCapability typed)
            {
                capability = typed;
                return true;
            }

            capability = null;
            return false;
        }
    }

    private sealed class DefaultPluginContextLease : IPluginContextLease
    {
        private readonly PluginTaskRegistry _tasks;
        private readonly IReadOnlyList<IRevocablePluginCapability> _revocable;
        private int _revoked;

        public DefaultPluginContextLease(
            IIdvbPluginContext context,
            PluginTaskRegistry tasks,
            IEnumerable<IPluginCapability> capabilities)
        {
            Context = context;
            _tasks = tasks;
            _revocable = capabilities.OfType<IRevocablePluginCapability>().ToArray();
        }

        public IIdvbPluginContext Context { get; }

        public async ValueTask RevokeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _revoked, 1) != 0)
            {
                return;
            }

            foreach (var capability in _revocable)
            {
                await capability.RevokeAsync(cancellationToken);
            }

            await _tasks.DisposeAsync();
        }

        public ValueTask DisposeAsync() => RevokeAsync(CancellationToken.None);
    }
}

public sealed class DelegatePluginLogger(Action<PluginLogLevel, string, Exception?> write) : IPluginLogger
{
    public void Log(PluginLogLevel level, string message, Exception? exception = null) =>
        write(level, message, exception);
}
