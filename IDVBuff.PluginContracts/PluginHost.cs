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

    public PluginHost(IMessageBus bus, IPluginContextFactory contextFactory)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

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
        return _byId.TryGetValue(id, out var registration) && registration.Enabled;
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

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        try
        {
            // 先加载所有插件；启用阶段可被页面重复调用。
            foreach (var registration in _registrations)
            {
                registration.Context = _contextFactory.Create(registration.Plugin);
                registration.Plugin.OnLoad(registration.Context);
            }

            foreach (var registration in _registrations)
            {
                if (registration.DesiredEnabled)
                    EnableRegistration(registration);
            }
        }
        catch
        {
            _started = false;
            foreach (var registration in _registrations.AsEnumerable().Reverse())
            {
                DisableRegistration(registration);
                try
                {
                    registration.Plugin.OnUnload();
                }
                catch (Exception exception)
                {
                    registration.Context?.Logger.Error($"OnUnload 异常：{exception}");
                }
            }
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
        if (registration.Enabled == enabled)
            return;

        if (enabled)
        {
            EnableRegistration(registration);
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
                registration.Plugin.OnTick();
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
                registration.Plugin.OnUnload();
            }
            catch (Exception exception)
            {
                registration.Context?.Logger.Error($"OnUnload 异常：{exception}");
            }
        }
    }

    private void EnableRegistration(Registration registration)
    {
        var enabledCallbackEntered = false;
        var subscribed = false;
        try
        {
            registration.Plugin.OnEnable();
            enabledCallbackEntered = true;
            foreach (var type in registration.MessageTypes)
                InvokeSubscription(true, type, registration.Plugin);
            subscribed = true;
            registration.Plugin.OnStart();
            registration.Enabled = true;
        }
        catch
        {
            if (subscribed)
            {
                foreach (var type in registration.MessageTypes)
                    InvokeSubscription(false, type, registration.Plugin);
            }
            if (enabledCallbackEntered)
            {
                try
                {
                    registration.Plugin.OnDisable();
                }
                catch (Exception exception)
                {
                    registration.Context?.Logger.Error($"OnDisable 异常：{exception}");
                }
            }
            registration.Enabled = false;
            throw;
        }
    }

    private void DisableRegistration(Registration registration)
    {
        if (!registration.Enabled)
            return;

        foreach (var type in registration.MessageTypes)
            InvokeSubscription(false, type, registration.Plugin);
        try
        {
            registration.Plugin.OnDisable();
        }
        catch (Exception exception)
        {
            registration.Context?.Logger.Error($"OnDisable 异常：{exception}");
        }
        registration.Enabled = false;
    }

    public void Dispose() => Stop();

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
        }

        public IPlugin Plugin { get; }

        public IReadOnlyList<Type> MessageTypes { get; }

        public IPluginContext? Context { get; set; }

        public bool DesiredEnabled { get; set; }

        public bool Enabled { get; set; }
    }
}
