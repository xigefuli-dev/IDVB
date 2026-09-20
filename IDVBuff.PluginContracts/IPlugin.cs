namespace IDVBuff.PluginContracts;

/// <summary>
/// 插件生命周期契约。OnLoad 恰好一次；OnEnable/OnDisable 可多次；
/// OnTick 由宿主定时驱动；OnStart/OnDisable 标定消息订阅窗口。
/// </summary>
// Retained as a supported compatibility contract for compiled-in built-in plugins.
// Third-party plugins should implement IIdvbPlugin from IdentityVisionBridge.PluginSdk.
public interface IPlugin
{
    string Id { get; }

    string DisplayName { get; }

    void OnLoad(IPluginContext context);

    void OnEnable();

    void OnStart();

    void OnTick();

    void OnDisable();

    void OnUnload();
}

/// <summary>
/// 提供空实现的便捷基类，暴露受保护的 <see cref="Context"/>。
/// </summary>
public abstract class PluginBase : IPlugin
{
    protected IPluginContext Context { get; private set; } = null!;

    public abstract string Id { get; }

    public virtual string DisplayName => GetType().Name;

    public virtual void OnLoad(IPluginContext context) => Context = context;

    public virtual void OnEnable() { }

    public virtual void OnStart() { }

    public virtual void OnTick() { }

    public virtual void OnDisable() { }

    public virtual void OnUnload() { }
}
