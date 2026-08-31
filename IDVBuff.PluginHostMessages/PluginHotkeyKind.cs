namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 宿主全局热键的语义种类，对应宿主 <c>IGlobalInput</c> 的全局热键事件。
/// 消息契约自带的枚举，不绑定宿主内部类型。
/// </summary>
public enum PluginHotkeyKind
{
    QuickScan,
    OverlayToggle,
    ManualRecognition,
    GameMapToggle,
    ControlPanelToggle,
    SwitchFloor,
    SaveMapCache,
    RestMapDisplay,
    Alt
}
