using IDVBuff.Features.Maps;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// <see cref="IPluginLogger"/> 的宿主实现，桥到进程级
/// <see cref="MapLogCollector.Instance"/>（与识别组件共享同一日志会话）。
/// </summary>
public sealed class PluginLogger : IPluginLogger
{
    private readonly string _pluginId;

    public PluginLogger(string pluginId)
    {
        _pluginId = pluginId;
    }

    public void Info(string message, IReadOnlyDictionary<string, object?>? details = null) =>
        Append(MapLogLevel.Info, message, details);

    public void Warning(string message, IReadOnlyDictionary<string, object?>? details = null) =>
        Append(MapLogLevel.Warning, message, details);

    public void Error(string message, IReadOnlyDictionary<string, object?>? details = null) =>
        Append(MapLogLevel.Error, message, details);

    private void Append(
        MapLogLevel level,
        string message,
        IReadOnlyDictionary<string, object?>? details)
    {
        var merged = new Dictionary<string, object?> { ["pluginId"] = _pluginId };
        if (details is not null)
        {
            foreach (var pair in details)
                merged[pair.Key] = pair.Value;
        }

        MapLogCollector.Instance.Append(
            MapLogCategory.Plugin,
            level,
            message,
            details: merged);
    }
}
