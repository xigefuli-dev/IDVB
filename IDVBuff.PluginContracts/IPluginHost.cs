namespace IDVBuff.PluginContracts;

/// <summary>
/// 注册表（供组合根 / 插件管理页查询）。
/// </summary>
public interface IPluginRegistry
{
    IReadOnlyList<IPlugin> Plugins { get; }

    bool TryGet(string id, out IPlugin? plugin);

    IPlugin GetRequired(string id);

    bool IsEnabled(string id);
}

/// <summary>
/// 宿主对插件的控制面。
/// </summary>
public interface IPluginHost
{
    void Register(IPlugin plugin);

    void SetEnabled(string id, bool enabled);

    void Start();

    void Tick();

    void Stop();
}
