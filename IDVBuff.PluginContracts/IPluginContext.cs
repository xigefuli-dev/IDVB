namespace IDVBuff.PluginContracts;

/// <summary>
/// 插件上下文：向插件暴露宿主能力。
/// </summary>
public interface IPluginContext
{
    string PluginId { get; }

    string PluginDisplayName { get; }

    IPluginLogger Logger { get; }

    IMessageBus Messages { get; }

    IPluginSynchronizer UI { get; }

    object? GetService(Type serviceType);

    T? GetService<T>() where T : class;
}

/// <summary>
/// 宿主实现，为每个插件创建上下文。
/// </summary>
public interface IPluginContextFactory
{
    IPluginContext Create(IPlugin plugin);
}
