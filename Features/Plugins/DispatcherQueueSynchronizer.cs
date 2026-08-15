using IDVBuff.PluginContracts;
using Microsoft.UI.Dispatching;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// <see cref="IPluginSynchronizer"/> 的宿主实现，包装 UI 线程的
/// <see cref="DispatcherQueue"/>。SDK 本身不引用 WindowsAppSDK。
/// </summary>
public sealed class DispatcherQueueSynchronizer : IPluginSynchronizer
{
    private readonly DispatcherQueue _dispatcher;

    public DispatcherQueueSynchronizer(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool HasThreadAccess => _dispatcher.HasThreadAccess;

    public bool TryPost(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.TryEnqueue(new DispatcherQueueHandler(action));
    }

    public bool TryPost<T>(Action<T> action, T state)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.TryEnqueue(() => action(state));
    }
}
