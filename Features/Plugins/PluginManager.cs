using IDVBuff.Diagnostics;
using IDVBuff.PluginContracts;
using Microsoft.UI.Dispatching;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// WinUI 适配器：组合框架无关的 SDK <see cref="PluginHost"/>，并用
/// <see cref="DispatcherQueueTimer"/> 在 UI 线程定时驱动 <see cref="IPlugin.OnTick"/>。
/// </summary>
public sealed class PluginManager : IPluginHost, IPluginRegistry, IDisposable
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(250);

    private readonly PluginHost _host;
    private readonly DispatcherQueueTimer _tickTimer;
    private readonly PluginPreferencesStore _preferences;

    public PluginManager(
        DispatcherQueue dispatcher,
        IMessageBus bus,
        IPluginContextFactory contextFactory,
        TimeSpan? tickInterval = null,
        PluginPreferencesStore? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _host = new PluginHost(bus, contextFactory);
        _preferences = preferences ?? new PluginPreferencesStore();
        _tickTimer = dispatcher.CreateTimer();
        _tickTimer.Interval = tickInterval ?? DefaultTickInterval;
        _tickTimer.Tick += (_, _) => Tick();
    }

    public IReadOnlyList<IPlugin> Plugins => _host.Plugins;

    public bool TryGet(string id, out IPlugin? plugin) => _host.TryGet(id, out plugin);

    public IPlugin GetRequired(string id) => _host.GetRequired(id);

    public bool IsEnabled(string id) => _host.IsEnabled(id);

    public bool IsActive(string id) => _host.IsActive(id);

    /// <summary>
    /// Controls the match-scoped activation gate. It never alters the primary
    /// enablement switches persisted by <see cref="PluginPreferencesStore"/>.
    /// </summary>
    public void SetMatchActivation(bool active) => _host.SetActivationAllowed(active);

    public void Register(IPlugin plugin) =>
        _host.Register(plugin, _preferences.IsEnabled(plugin.Id));

    public void SetEnabled(string id, bool enabled)
    {
        var previous = _host.IsEnabled(id);
        _host.SetEnabled(id, enabled);

        try
        {
            _preferences.SetEnabled(id, enabled);
        }
        catch
        {
            // Keep the in-memory lifecycle and the persisted preference in
            // sync if the local file cannot be written.
            try
            {
                _host.SetEnabled(id, previous);
            }
            catch
            {
                // Preserve the original persistence exception.
            }

            throw;
        }
    }

    public void Start()
    {
        // 设置回填挂在「插件上下文建立之后、启用之前」这个点上（PluginHost.ContextInitialized）：
        // 插件在 OnLoad 里已经拿到宿主服务，所以回填时能读到地图库这类需要宿主的数据。
        // 若放在这之前（旧写法：先循环 RestoreSettings 再 _host.Start()），
        // 「候选来自宿主」的下拉类设置会因为候选为空而被宿主回退成默认项，
        // 用户保存的选择等于每次都丢。
        _host.ContextInitialized = plugin =>
        {
            if (plugin is not IPluginSettingsProvider provider)
                return;
            try
            {
                using (StartupTimeline.Measure($"Built-in restore settings: {plugin.Id}"))
                    _preferences.RestoreSettings(provider, plugin.Id);
            }
            catch
            {
                // 单个插件的坏设置不能挡住宿主启动。
            }
        };
        using (StartupTimeline.Measure("Built-in host Start (lifecycle callbacks)"))
            _host.Start();
        using (StartupTimeline.Measure("Built-in dispatcher timer Start"))
            _tickTimer.Start();
    }

    public void Tick() => _host.Tick();

    public void Stop()
    {
        _tickTimer.Stop();
        _host.Stop();
    }

    public void Dispose() => Stop();
}
