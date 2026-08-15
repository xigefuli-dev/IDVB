using IDVBuff.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// <see cref="IPluginContext"/> 的宿主实现。服务定位桥到宿主 DI 容器——
/// 插件只能解析 Core / Survey / Services 的契约接口，不能解析 Features 具体类型。
/// </summary>
public sealed class PluginContext : IPluginContext
{
    private readonly IServiceProvider _services;

    public PluginContext(
        string pluginId,
        string pluginDisplayName,
        IPluginLogger logger,
        IMessageBus messages,
        IPluginSynchronizer ui,
        IServiceProvider services)
    {
        PluginId = pluginId;
        PluginDisplayName = pluginDisplayName;
        Logger = logger;
        Messages = messages;
        UI = ui;
        _services = services;
    }

    public string PluginId { get; }

    public string PluginDisplayName { get; }

    public IPluginLogger Logger { get; }

    public IMessageBus Messages { get; }

    public IPluginSynchronizer UI { get; }

    public object? GetService(Type serviceType) => _services.GetService(serviceType);

    public T? GetService<T>() where T : class => _services.GetService<T>();
}
