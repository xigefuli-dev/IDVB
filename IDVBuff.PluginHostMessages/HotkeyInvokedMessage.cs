namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 宿主全局热键被触发。
/// </summary>
public sealed record HotkeyInvokedMessage(PluginHotkeyKind Kind);
