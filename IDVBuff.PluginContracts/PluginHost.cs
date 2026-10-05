using System.Reflection;

namespace IDVBuff.PluginContracts;

/// <summary>
/// 框架无关的插件生命周期运行时。供宿主与单元测试复用。
/// </summary>
public sealed class PluginHost : IPluginHost, IPluginRegistry, IDisposable
{
    private readonly IMessageBus _bus;
    private readonly IPluginContextFactory _contextFactory;
    private readonly List<Registration> _registrations = new();
    private readonly Dictionary<string, Registration> _byId = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    // The desktop host keeps this closed until the user explicitly activates
    // plugins for a started match. The default preserves the standalone host
    // contract used by non-desktop hosts and tests.
    private bool _activationAllowed = true;

    public PluginHost(IMessageBus bus, IPluginContextFactory contextFactory)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>
    /// 在插件上下文建立之后、启用之前回调（每个插件一次）。宿主用它把持久化设置
    /// 回填给插件：此时 <see cref="IPlugin.OnLoad"/> 已经跑过——插件已经能拿到宿主
    /// 服务（例如地图库），因此「候选来自宿主」的下拉类设置不会因为候选为空而被
    /// 回退成默认项——而 <see cref="IPlugin.OnEnable"/> 还没跑，插件启用时看到的
    /// 就是用户保存的值。
    /// </summary>
    public Action<IPlugin>? ContextInitialized { get; set; }

    public IReadOnlyList<IPlugin> Plugins =>
        _registrations.Select(r => r.Plugin).ToList();

    public bool TryGet(string id, out IPlugin? plugin)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_byId.TryGetValue(id, out var registration))
        {
            plugin = registration.Plugin;
            return true;
        }
        plugin = null;
        return false;
    }

    public IPlugin GetRequired(string id)
    {
        if (!TryGet(id, out var plugin))
            throw new KeyNotFoundException($"未注册的插件 Id：{id}");
        return plugin!;
    }

    public bool IsEnabled(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.TryGetValue(id, out var registration) && registration.DesiredEnabled;
    }

    /// <summary>Whether a plugin is currently receiving lifecycle callbacks.</summary>
    public bool IsActive(string id) =>
        _byId.TryGetValue(id, out var registration) && registration.Enabled;

    /// <summary>
    /// Opens or closes runtime activation without changing the user's saved
    /// per-plugin enablement choices.
    /// </summary>
    public void SetActivationAllowed(bool allowed)
    {
        if (_activationAllowed == allowed)
            return;

        if (!allowed)
        {
            foreach (var registration in _registrations)
            {
                if (!registration.IsAlwaysActive)
                    DisableRegistration(registration);
            }
            _activationAllowed = false;
            return;
        }

        _activationAllowed = true;
        var activated = new List<Registration>();
        try
        {
            if (_started)
            {
                foreach (var registration in _registrations.Where(static item => item.DesiredEnabled))
                {
                    if (registration.IsAlwaysActive && registration.Enabled)
                        continue;
                    InitializeRegistration(registration);
                    EnableRegistration(registration);
                    activated.Add(registration);
                }
            }
        }
        catch
        {
            foreach (var registration in activated.AsEnumerable().Reverse())
                DisableRegistration(registration);
            _activationAllowed = false;
            throw;
        }
    }

    public void Register(IPlugin plugin) => Register(plugin, initiallyEnabled: true);

    /// <summary>
    /// Registers a plugin and specifies whether it should be enabled when the
    /// host next starts. This lets the host restore persisted user choices
    /// before plugin lifecycle callbacks run.
    /// </summary>
    public void Register(IPlugin plugin, bool initiallyEnabled)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (string.IsNullOrWhiteSpace(plugin.Id))
            throw new InvalidOperationException("插件 Id 不能为空。");
        if (_started)
            throw new InvalidOperationException("插件宿主已启动，不能再注册插件。");
        if (_byId.ContainsKey(plugin.Id))
            throw new InvalidOperationException(
                $"重复的插件 Id（大小写不敏感）：{plugin.Id}");

        var registration = new Registration(
            plugin,
            MessageBus.GetHandlerMessageTypes(plugin),
            initiallyEnabled);
        _registrations.Add(registration);
        _byId[plugin.Id] = registration;
    }

    public void Start() => Start(CancellationToken.None);

    public void Start(CancellationToken cancellationToken)
    {
        if (_started)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;

        try
        {
            // Before entering a match, do not create plugin contexts unless the
            // plugin is marked as AlwaysActive to run across match boundaries.
            foreach (var registration in _registrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_activationAllowed || registration.IsAlwaysActive)
                {
                    InitializeRegistration(registration, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (registration.DesiredEnabled)
                        EnableRegistration(registration, cancellationToken);
                }
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_started)
            throw new InvalidOperationException("插件宿主尚未启动。");
        if (!_byId.TryGetValue(id, out var registration))
            throw new KeyNotFoundException($"未注册的插件 Id：{id}");
        if (registration.DesiredEnabled == enabled)
            return;

        if (enabled)
        {
            if (_activationAllowed || registration.IsAlwaysActive)
            {
                InitializeRegistration(registration);
                EnableRegistration(registration);
            }
            registration.DesiredEnabled = true;
        }
        else
        {
            DisableRegistration(registration);
            registration.DesiredEnabled = false;
        }
    }

    public void Tick()
    {
        if (!_started)
            return;
        foreach (var registration in _registrations)
        {
            if (!registration.Enabled)
                continue;
            try
            {
                registration.Adapter?.Tick();
            }
            catch (Exception exception)
            {
                registration.Context?.Logger.Error($"OnTick 异常：{exception}");
            }
        }
    }

    public void Stop()
    {
        if (!_started)
            return;
        _started = false;

        for (var i = _registrations.Count - 1; i >= 0; i--)
        {
            var registration = _registrations[i];
            // 先退订并停用，再逆序卸载，避免卸载期间收到消息。
            DisableRegistration(registration);
            try
            {
                registration.Adapter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                registration.Context?.Logger.Error($"OnUnload 异常：{exception}");
            }
            finally
            {
                registration.Adapter = null;
                registration.Context = null;
                registration.SdkContext = null;
            }
        }
    }

    private void EnableRegistration(Registration registration, CancellationToken cancellationToken = default)
    {
        try
        {
            (registration.Adapter ?? throw new InvalidOperationException("Plugin adapter is not initialized."))
                .StartAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
            registration.Enabled = true;
        }
        catch
        {
            registration.Enabled = false;
            throw;
        }
    }

    private void InitializeRegistration(Registration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registration.Adapter is not null)
            return;

        registration.Context = _contextFactory.Create(registration.Plugin);
        registration.SdkContext = new LegacyPluginV2Context(
            registration.Plugin,
            registration.Context);
        registration.Adapter = new LegacyPluginV2CompatibilityAdapter(
            registration.Plugin,
            () => Subscribe(registration),
            () => Unsubscribe(registration));
        registration.Adapter.InitializeAsync(registration.SdkContext, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        ContextInitialized?.Invoke(registration.Plugin);
    }

    private void DisableRegistration(Registration registration)
    {
        if (!registration.Enabled)
            return;

        try
        {
            registration.Adapter?.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            registration.Context?.Logger.Error($"OnDisable 异常：{exception}");
        }
        registration.Enabled = false;
    }

    public void Dispose() => Stop();

    private void Subscribe(Registration registration)
    {
        foreach (var type in registration.MessageTypes)
            InvokeSubscription(true, type, registration.Plugin);
    }

    private void Unsubscribe(Registration registration)
    {
        foreach (var type in registration.MessageTypes)
            InvokeSubscription(false, type, registration.Plugin);
    }

    private void InvokeSubscription(bool subscribe, Type messageType, object plugin)
    {
        var methodName = subscribe
            ? nameof(IMessageBus.Subscribe)
            : nameof(IMessageBus.Unsubscribe);
        var method = typeof(IMessageBus).GetMethod(methodName)!.MakeGenericMethod(messageType);
        method.Invoke(_bus, new[] { plugin });
    }

    private sealed class Registration
    {
        public Registration(
            IPlugin plugin,
            IReadOnlyList<Type> messageTypes,
            bool initiallyEnabled)
        {
            Plugin = plugin;
            MessageTypes = messageTypes;
            DesiredEnabled = initiallyEnabled;
            var attribute = plugin.GetType().GetCustomAttribute<PluginAttribute>();
            IsAlwaysActive = attribute?.AlwaysActive ?? false;
        }

        public IPlugin Plugin { get; }

        public IReadOnlyList<Type> MessageTypes { get; }

        public bool IsAlwaysActive { get; }

        public IPluginContext? Context { get; set; }

        public LegacyPluginV2Context? SdkContext { get; set; }

        public LegacyPluginV2CompatibilityAdapter? Adapter { get; set; }

        public bool DesiredEnabled { get; set; }

        public bool Enabled { get; set; }
    }
}
