namespace IDVBuff.PluginContracts;

/// <summary>
/// 强类型消息处理器。消息类型 <typeparamref name="TMessage"/> 为不可变 DTO。
/// </summary>
public interface IHandle<in TMessage>
{
    void Handle(TMessage message);
}
