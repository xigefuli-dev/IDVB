namespace IDVBuff.PluginContracts;

/// <summary>
/// 插件日志抽象。宿主实现负责桥接到宿主日志系统。
/// </summary>
public interface IPluginLogger
{
    void Info(string message, IReadOnlyDictionary<string, object?>? details = null);

    void Warning(string message, IReadOnlyDictionary<string, object?>? details = null);

    void Error(string message, IReadOnlyDictionary<string, object?>? details = null);
}
