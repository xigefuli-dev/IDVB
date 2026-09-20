using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 将宿主的低层输入服务适配为 PluginSDK 的插件级输入通道。
/// </summary>
public sealed class PluginInputService : IPluginInputService, IDisposable
{
    private readonly IGlobalInput _input;

    public PluginInputService(IGlobalInput input)
    {
        _input = input;
        _input.PluginInputInvoked += OnPluginInputInvoked;
    }

    public event EventHandler<PluginInputEventArgs>? BindingInvoked;

    public void SetBinding(string pluginId, string bindingKey, PluginInputBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKey);
        ArgumentNullException.ThrowIfNull(binding);
        // Keep an unconfigured value for this binding key so changing one
        // setting cannot erase another binding owned by the same plugin.
        _input.ApplyPluginBinding(pluginId, bindingKey, binding);
    }

    public void ClearBindings(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _input.ClearPluginBindings(pluginId);
    }

    public bool IsBindingPressed(string pluginId, string bindingKey) =>
        !string.IsNullOrWhiteSpace(pluginId)
        && !string.IsNullOrWhiteSpace(bindingKey)
        && _input.IsPluginBindingPressed(pluginId, bindingKey);

    public void Dispose()
    {
        _input.PluginInputInvoked -= OnPluginInputInvoked;
    }

    private void OnPluginInputInvoked(
        object? sender,
        PluginInputInvokedEventArgs args) =>
        BindingInvoked?.Invoke(
            this,
            new PluginInputEventArgs(
                args.PluginId,
                args.BindingKey,
                args.Timestamp,
                args.IsDown));
}
