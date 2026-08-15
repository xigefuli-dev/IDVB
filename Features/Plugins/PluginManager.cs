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
        _host.Start();
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
