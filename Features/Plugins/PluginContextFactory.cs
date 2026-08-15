using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// <see cref="IPluginContextFactory"/> 的宿主实现：为每个插件创建一个
/// <see cref="PluginContext"/>，共享总线、UI 同步器与 DI 容器。
/// </summary>
public sealed class PluginContextFactory : IPluginContextFactory
{
    private readonly IMessageBus _bus;
    private readonly IPluginSynchronizer _ui;
    private readonly IServiceProvider _services;

    public PluginContextFactory(
        IMessageBus bus,
        IPluginSynchronizer ui,
        IServiceProvider services)
    {
        _bus = bus;
        _ui = ui;
        _services = services;
    }

    public IPluginContext Create(IPlugin plugin) => new PluginContext(
        plugin.Id,
        plugin.DisplayName,
        new PluginLogger(plugin.Id),
        _bus,
        _ui,
        _services);
}
