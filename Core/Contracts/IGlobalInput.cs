// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

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

    /// <summary>
    /// 释放所有当前按下的键盘按键和鼠标按钮（用于叠加窗口取得焦点前的手动释放）。
    /// </summary>
    void ReleaseAllPressedInputs();
}
