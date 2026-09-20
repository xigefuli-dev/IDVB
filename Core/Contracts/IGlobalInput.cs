// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

/// <summary>
/// 全局鼠标滚轮输入。<see cref="CapsHeld"/> 表示事件发生时 Caps 键仍处于
/// 物理按下状态，不受 CapsLock 的切换状态影响。
/// </summary>
public sealed class MouseWheelInputEventArgs(
    long timestamp,
    int delta,
    bool capsHeld,
    IReadOnlySet<PluginInputBindingState>? pluginBindingStates = null) : EventArgs
{
    public long Timestamp { get; } = timestamp;

    public int Delta { get; } = delta;

    public bool CapsHeld { get; } = capsHeld;

    private IReadOnlySet<PluginInputBindingState> PluginBindingStates { get; } =
        pluginBindingStates ?? new HashSet<PluginInputBindingState>();

    /// <summary>
    /// Returns whether a plugin binding was physically pressed when this wheel
    /// input was captured. This remains valid after the event is queued to the
    /// UI thread and the user has already released the binding.
    /// </summary>
    public bool IsPluginBindingPressed(string pluginId, string bindingKey) =>
        PluginBindingStates.Contains(new PluginInputBindingState(pluginId, bindingKey));

    /// <summary>
    /// Returns whether this input can be merged with another contiguous wheel
    /// input without changing which plugin bindings were held at capture time.
    /// </summary>
    public bool CanCoalesceWith(MouseWheelInputEventArgs other) =>
        other is not null
        && CapsHeld == other.CapsHeld
        && PluginBindingStates.Count == other.PluginBindingStates.Count
        && PluginBindingStates.All(other.PluginBindingStates.Contains);

    /// <summary>Creates one wheel input containing the accumulated delta.</summary>
    public MouseWheelInputEventArgs Coalesce(MouseWheelInputEventArgs other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!CanCoalesceWith(other))
            throw new ArgumentException("Wheel inputs have different binding states.", nameof(other));

        return new MouseWheelInputEventArgs(
            other.Timestamp,
            (int)Math.Clamp(
                (long)Delta + other.Delta,
                int.MinValue,
                int.MaxValue),
            CapsHeld,
            PluginBindingStates);
    }
}

/// <summary>插件绑定在某个全局输入事件发生瞬间的物理按下快照。</summary>
public sealed record PluginInputBindingState(string PluginId, string BindingKey);

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

    /// <summary>强制结束当前对齐地图显示热键被触发。</summary>
    event EventHandler</* MapInputInvokedEventArgs */ object>? RestMapDisplayInvoked;

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
        object /* MapInputBinding */ saveMapCache,
        object /* MapInputBinding */ restMapDisplay);

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
