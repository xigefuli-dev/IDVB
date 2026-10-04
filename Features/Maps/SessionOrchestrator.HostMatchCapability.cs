using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 把宿主已有的对局控制通道暴露成 SDK 能力（<see cref="IHostMatchCapability"/>），
/// 供内置插件与第三方插件共用同一套语义。
///
/// 这里只做转发，不新增任何逻辑：真正的校验、调度器封送、地图缓存同步与
/// 生命周期锁都在 <see cref="SessionOrchestrator.QueuePluginMatchAsync"/> 里，
/// 与插件 SDK 的第三方插件路径（VisionCapabilityProvider.Control）行为一致。
///
/// 之所以要让内置插件走接口而不是直接解析 <c>SessionOrchestrator</c>：
/// 插件工程不能引用主程序集（会形成循环引用），而宿主约定插件只解析
/// Core / 契约接口（见 PluginContext 的注释）。
/// </summary>
public sealed partial class SessionOrchestrator : IHostMatchCapability
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetMapClassesAsync(CancellationToken cancellationToken = default) =>
        GetPluginMapClassesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<HostOperationResult> SwitchAndBeginAsync(
        HostMatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return QueuePluginMatchAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HostOperationResult> EndAsync(CancellationToken cancellationToken = default) =>
        QueuePluginMatchAsync(null, cancellationToken);

    /// <inheritdoc />
    public Task<HostOperationResult?> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetPluginMatchOperation(operationId));
    }
}
