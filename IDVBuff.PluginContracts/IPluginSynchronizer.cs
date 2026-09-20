namespace IDVBuff.PluginContracts;

/// <summary>
/// 宿主 UI 线程抽象。SDK 不引用 WindowsAppSDK，由宿主适配 DispatcherQueue。
/// </summary>
public interface IPluginSynchronizer
{
    bool HasThreadAccess { get; }

    bool TryPost(Action action);

    bool TryPost<T>(Action<T> action, T state);
}
