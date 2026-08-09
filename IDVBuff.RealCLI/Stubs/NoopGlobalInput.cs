// IDVB Real CLI — 空操作全局输入
// 实现 IGlobalInput，不安装任何 Win32 键盘/鼠标钩子。
// 所有事件声明但永不触发，ApplyBindings/ClearBindings 为空操作。

using IDVBuff.Core.Contracts;

namespace IDVBuff.RealCLI.Stubs;

/// <summary>
/// 空操作全局输入。不安装系统钩子，在 CLI 环境中安全运行。
/// </summary>
public sealed class NoopGlobalInput : IGlobalInput
{
#pragma warning disable CS0067 // 事件从未被使用——这是预期行为
    public event EventHandler<object>? QuickScanInvoked;
    public event EventHandler<object>? OverlayToggleInvoked;
    public event EventHandler<object>? ManualRecognitionInvoked;
    public event EventHandler<object>? GameMapToggleInvoked;
    public event EventHandler<object>? ControlPanelToggleInvoked;
    public event EventHandler<object>? SwitchFloorInvoked;
    public event EventHandler<object>? SaveMapCacheInvoked;
#pragma warning restore CS0067

    public void ApplyBindings(
        object quickScan,
        object overlayToggle,
        object manualRecognition,
        object gameMapToggle,
        object controlPanelToggle,
        object switchFloor,
        object saveMapCache)
    {
        // 空操作：CLI 不需要热键绑定
    }

    public void ClearBindings() { }
    public void ReleaseAllPressedInputs() { }
    public void Dispose() { }
}
