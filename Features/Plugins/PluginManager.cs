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
    public event EventHandler? EnabledChanged;

    public bool TryGet(string id, out IPlugin? plugin) => _host.TryGet(id, out plugin);

    public IPlugin GetRequired(string id) => _host.GetRequired(id);

    public bool IsEnabled(string id) => _host.IsEnabled(id);

    public bool IsActive(string id) => _host.IsActive(id);

    /// <summary>
    /// Controls the match-scoped activation gate. It never alters the primary
    /// enablement switches persisted by <see cref="PluginPreferencesStore"/>.
    /// </summary>
    public void SetMatchActivation(bool active) => _host.SetActivationAllowed(active);

    public void Register(IPlugin plugin)
    {
        if (plugin is IPluginSettingsProvider provider)
            using (StartupTimeline.Measure($"Built-in restore settings: {plugin.Id}"))
                _preferences.RestoreSettings(provider, plugin.Id);
        var enabled = _preferences.IsEnabled(plugin.Id);
        if (plugin is IDVBuff.Plugins.IdvLogin.IdvLoginPlugin login && !login.HasValidPath)
        {
            if (enabled) _preferences.SetEnabled(plugin.Id, false);
            enabled = false;
        }
        // Initial desired state belongs to registration, before host.Start().
        _host.Register(plugin, enabled);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (enabled && _host.TryGet(id, out var candidate) &&
            candidate is IDVBuff.Plugins.IdvLogin.IdvLoginPlugin login && !login.HasValidPath)
            throw new InvalidOperationException("请先填写 idv-login 的实际路径。");
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
        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
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
