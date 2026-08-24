using System.Reflection;
using System.Text.Json;
using IdentityVisionBridge.PluginSdk;
using SdkLogger = IdentityVisionBridge.PluginSdk.IPluginLogger;

namespace IDVBuff.PluginContracts;

#pragma warning disable CS0618

/// <summary>
/// One-release internal bridge that executes built-in <see cref="IPlugin"/> instances through
/// the V2 asynchronous lifecycle without exposing the legacy host surface to IDVP plugins.
/// </summary>
internal sealed class LegacyPluginV2CompatibilityAdapter : IIdvbPlugin
{
    private readonly IPlugin _plugin;
    private readonly Action _subscribe;
    private readonly Action _unsubscribe;
    private LegacyPluginV2Context? _context;
    private bool _started;
    private bool _disposed;

    public LegacyPluginV2CompatibilityAdapter(
        IPlugin plugin,
        Action subscribe,
        Action unsubscribe)
    {
        _plugin = plugin;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
    }

    public ValueTask InitializeAsync(
        IIdvbPluginContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context is not LegacyPluginV2Context legacyContext)
            throw new InvalidOperationException("The built-in compatibility context is missing.");
        _context = legacyContext;
        _plugin.OnLoad(legacyContext.LegacyContext);
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return ValueTask.CompletedTask;

        var enabledCallbackCompleted = false;
        var subscribed = false;
        try
        {
            _plugin.OnEnable();
            enabledCallbackCompleted = true;
            _subscribe();
            subscribed = true;
            _plugin.OnStart();
            _started = true;
            return ValueTask.CompletedTask;
        }
        catch
        {
            if (subscribed)
                _unsubscribe();
            if (enabledCallbackCompleted)
            {
                try
                {
                    _plugin.OnDisable();
                }
                catch (Exception exception)
                {
                    _context?.LegacyContext.Logger.Error($"OnDisable 异常：{exception}");
                }
            }
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        await (_context?.StopTasksAsync(cancellationToken) ?? ValueTask.CompletedTask);
        _unsubscribe();
        try
        {
            _plugin.OnDisable();
        }
        finally
        {
            _started = false;
        }
    }

    public void Tick() => _plugin.OnTick();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_started)
            await StopAsync(CancellationToken.None);
        try
        {
            _plugin.OnUnload();
        }
        finally
        {
            if (_context is not null)
                await _context.DisposeAsync();
            _context = null;
        }
    }
}

internal sealed class LegacyPluginV2Context : IIdvbPluginContext, IAsyncDisposable
{
    private readonly LegacyPluginTaskRegistry _tasks = new();
    private readonly LegacyBuiltInHostCapability _hostCapability;

    public LegacyPluginV2Context(IPlugin plugin, IPluginContext legacyContext)
    {
        LegacyContext = legacyContext;
        var metadata = plugin.GetType().GetCustomAttribute<PluginAttribute>();
        Identity = new PluginIdentity
        {
            Id = plugin.Id,
            DisplayName = plugin.DisplayName,
            Version = metadata?.Version ?? "legacy",
            PublisherId = "idvb.builtin"
        };
        Logger = new LegacyPluginLogger(legacyContext.Logger);
        Settings = new LegacyPluginSettings(plugin);
        _hostCapability = new LegacyBuiltInHostCapability(legacyContext);
    }

    public IPluginContext LegacyContext { get; }

    public PluginIdentity Identity { get; }

    public SdkLogger Logger { get; }

    public IPluginSettings Settings { get; }

    public IPluginTaskRegistry Tasks => _tasks;

    public bool TryGetCapability<TCapability>(out TCapability? capability)
        where TCapability : class, IPluginCapability
    {
        capability = _hostCapability as TCapability;
        return capability is not null;
    }

    public ValueTask StopTasksAsync(CancellationToken cancellationToken) =>
        _tasks.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _tasks.DisposeAsync();

    private sealed class LegacyPluginLogger(IPluginLogger logger) : SdkLogger
    {
        public void Log(PluginLogLevel level, string message, Exception? exception = null)
        {
            var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
            if (level >= PluginLogLevel.Error)
                logger.Error(text);
            else if (level >= PluginLogLevel.Warning)
                logger.Warning(text);
            else
                logger.Info(text);
        }
    }

    private sealed class LegacyPluginSettings(IPlugin plugin) : IPluginSettings
    {
        public PluginSettingsSnapshot Current
        {
            get
            {
                if (plugin is not IPluginSettingsProvider provider)
                    return new PluginSettingsSnapshot(new Dictionary<string, JsonElement>());
                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var definition in provider.Settings)
                {
                    var value = provider.GetSettingValue(definition.Key);
                    values[definition.Key] = JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));
                }
                return new PluginSettingsSnapshot(values);
            }
        }

        public IDisposable Subscribe(Action<PluginSettingsChanged> handler) => EmptySubscription.Instance;
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static EmptySubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class LegacyBuiltInHostCapability(IPluginContext context) : IPluginCapability
{
    public IPluginContext Context { get; } = context;
}

internal sealed class LegacyPluginTaskRegistry : IPluginTaskRegistry, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<LegacyPluginTaskHandle> _tasks = [];
    private CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public PluginTaskHandle Run(string name, Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handle = new LegacyPluginTaskHandle(name, operation, _lifetime.Token, Remove);
            _tasks.Add(handle);
            handle.Start();
            return handle;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        LegacyPluginTaskHandle[] tasks;
        CancellationTokenSource previousLifetime;
        lock (_sync)
        {
            if (_disposed)
                return;
            previousLifetime = _lifetime;
            _lifetime = new CancellationTokenSource();
            previousLifetime.Cancel();
            tasks = _tasks.ToArray();
        }
        foreach (var task in tasks)
            await task.StopAsync(cancellationToken);
        previousLifetime.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private void Remove(LegacyPluginTaskHandle handle)
    {
        lock (_sync)
            _tasks.Remove(handle);
    }

    private sealed class LegacyPluginTaskHandle : PluginTaskHandle
    {
        private readonly Func<CancellationToken, Task> _operation;
        private readonly CancellationTokenSource _cancellation;
        private readonly Action<LegacyPluginTaskHandle> _completed;
        private Task _completion = Task.CompletedTask;

        public LegacyPluginTaskHandle(
            string name,
            Func<CancellationToken, Task> operation,
            CancellationToken lifetime,
            Action<LegacyPluginTaskHandle> completed)
        {
            Name = name;
            _operation = operation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _completed = completed;
        }

        public override string Name { get; }

        public override Task Completion => _completion;

        public void Start()
        {
            _completion = Task.Run(() => _operation(_cancellation.Token));
            _ = _completion.ContinueWith(
                _ => _completed(this),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            try
            {
                await _completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None);
            _cancellation.Dispose();
        }
    }
}

#pragma warning restore CS0618
