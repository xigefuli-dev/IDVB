// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

/// <summary>
/// 全局鼠标滚轮输入。<see cref="CapsHeld"/> 表示事件发生时 Caps 键仍处于
/// 物理按下状态，不受 CapsLock 的切换状态影响。
/// </summary>
public sealed class MouseWheelInputEventArgs(long timestamp, int delta, bool capsHeld) : EventArgs
{
    public long Timestamp { get; } = timestamp;

    public int Delta { get; } = delta;

    public bool CapsHeld { get; } = capsHeld;
}

/// <summary>插件级绑定的全局按键状态变化。</summary>
public sealed class PluginInputInvokedEventArgs(
    string pluginId,
    string bindingKey,
    long timestamp,
    bool isDown) : EventArgs
{
    public string PluginId { get; } = pluginId;

    public string BindingKey { get; } = bindingKey;

    public long Timestamp { get; } = timestamp;

    public bool IsDown { get; } = isDown;
}

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 全局输入绑定抽象。通过低层键盘/鼠标钩子和键盘轮询，
/// 在游戏前台时仍然能捕获热键事件，并通过 DispatcherQueue 派发到 UI 线程。
/// </summary>
public interface IGlobalInput : IDisposable
{
    /// <summary>
    /// 快速扫描热键被触发。
    /// </summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? QuickScanInvoked;

    /// <summary>
    /// 叠加层切换热键被触发。
    /// </summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? OverlayToggleInvoked;

    /// <summary>
    /// 手动识别热键被触发。
    /// </summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? ManualRecognitionInvoked;

    /// <summary>
    /// 游戏地图开关热键被触发。
    /// </summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? GameMapToggleInvoked;

    /// <summary>
    /// 控制面板热键被触发。
    /// </summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? ControlPanelToggleInvoked;

    /// <summary>
    /// 切换楼层热键被触发。
    /// </summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? SwitchFloorInvoked;

    /// <summary>保存当前地图缩放缓存热键被触发。</summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? SaveMapCacheInvoked;

    /// <summary>Alt 键按下事件，供需要全局快捷键的插件使用。</summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? AltInvoked;

    /// <summary>全局鼠标滚轮事件，供需要组合键滚轮操作的插件使用。</summary>
    event EventHandler<MouseWheelInputEventArgs>? MouseWheelScrolled;

    /// <summary>插件级绑定的按下 / 抬起事件。</summary>
    event EventHandler<PluginInputInvokedEventArgs>? PluginInputInvoked;

    /// <summary>
    /// 应用新的按键绑定配置。
    /// </summary>
    void ApplyBindings(
        object /* MapInputBinding */ quickScan,
        object /* MapInputBinding */ overlayToggle,
        object /* MapInputBinding */ manualRecognition,
        object /* MapInputBinding */ gameMapToggle,
        object /* MapInputBinding */ controlPanelToggle,
        object /* MapInputBinding */ switchFloor,
        object /* MapInputBinding */ saveMapCache);

    /// <summary>
    /// 清除所有按键绑定并释放钩子。
    /// </summary>
    void ClearBindings();

    /// <summary>应用或替换一个插件级绑定。</summary>
    void ApplyPluginBinding(string pluginId, string bindingKey, object binding);

    /// <summary>清除指定插件注册的所有绑定。</summary>
    void ClearPluginBindings(string pluginId);

    /// <summary>查询插件绑定当前是否处于按下状态。</summary>
    bool IsPluginBindingPressed(string pluginId, string bindingKey);

    /// <summary>
    /// 释放所有当前按下的键盘按键和鼠标按钮（用于叠加窗口取得焦点前的手动释放）。
    /// </summary>
    void ReleaseAllPressedInputs();
}
