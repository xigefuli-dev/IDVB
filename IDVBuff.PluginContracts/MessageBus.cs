namespace IDVBuff.PluginContracts;

/// <summary>
/// 通用消息总线。
/// </summary>
public interface IMessageBus
{
    void Subscribe<TMessage>(IHandle<TMessage> handler);

    void Unsubscribe<TMessage>(IHandle<TMessage> handler);

    void Publish<TMessage>(TMessage message);
}

/// <summary>
/// 线程安全的通用实现。单个处理器抛异常被隔离，不影响其他处理器。
/// </summary>
[Obsolete("Legacy synchronous bus retained for built-in plugin compatibility only.")]
public sealed class MessageBus : IMessageBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<object>> _subscriptions = new();

    public void Subscribe<TMessage>(IHandle<TMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TMessage), out var list))
            {
                list = new List<object>();
                _subscriptions[typeof(TMessage)] = list;
            }
            if (!list.Contains(handler))
                list.Add(handler);
        }
    }

    public void Unsubscribe<TMessage>(IHandle<TMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(typeof(TMessage), out var list))
                list.Remove(handler);
        }
    }

    public void Publish<TMessage>(TMessage message)
    {
        List<object>? snapshot;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TMessage), out var list))
                return;
            snapshot = new List<object>(list);
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((IHandle<TMessage>)handler).Handle(message);
            }
            catch
            {
                // 单个处理器抛异常被隔离；异常由宿主插件层统一记录。
            }
        }
    }

    /// <summary>
    /// 反射返回对象实现的所有已闭合 <see cref="IHandle{TMessage}"/> 的消息类型；
    /// 忽略非 IHandle 接口、开放泛型定义、以及 <c>IHandle{object}</c>。
    /// </summary>
    public static IReadOnlyList<Type> GetHandlerMessageTypes(object handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var result = new List<Type>();
        foreach (var type in handler.GetType().GetInterfaces())
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IHandle<>))
                continue;
            var argument = type.GetGenericArguments()[0];
            if (argument == typeof(object))
                continue;
            if (!result.Contains(argument))
                result.Add(argument);
        }
        return result;
    }
}
