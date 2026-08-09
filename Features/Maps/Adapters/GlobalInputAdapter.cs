using IDVBuff.Core.Contracts;
using Microsoft.UI.Dispatching;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IGlobalInput 适配器 — 委托给 MapGlobalInputService。</summary>
public sealed class GlobalInputAdapter : IGlobalInput
{
    private readonly MapGlobalInputService _input;

    public GlobalInputAdapter(DispatcherQueue dispatcher)
    {
        _input = new MapGlobalInputService(dispatcher);
        _input.QuickScanInvoked += (_, args) => QuickScanInvoked?.Invoke(this, args);
        _input.OverlayToggleInvoked += (_, args) => OverlayToggleInvoked?.Invoke(this, args);
        _input.ManualRecognitionInvoked += (_, args) => ManualRecognitionInvoked?.Invoke(this, args);
        _input.GameMapToggleInvoked += (_, args) => GameMapToggleInvoked?.Invoke(this, args);
        _input.ControlPanelToggleInvoked += (_, args) => ControlPanelToggleInvoked?.Invoke(this, args);
        _input.SwitchFloorInvoked += (_, args) => SwitchFloorInvoked?.Invoke(this, args);
        _input.SaveMapCacheInvoked += (_, args) => SaveMapCacheInvoked?.Invoke(this, args);
    }

    public event EventHandler<object>? QuickScanInvoked;
    public event EventHandler<object>? OverlayToggleInvoked;
    public event EventHandler<object>? ManualRecognitionInvoked;
    public event EventHandler<object>? GameMapToggleInvoked;
    public event EventHandler<object>? ControlPanelToggleInvoked;
    public event EventHandler<object>? SwitchFloorInvoked;
    public event EventHandler<object>? SaveMapCacheInvoked;

    public void ApplyBindings(object quickScan, object overlayToggle,
        object manualRecognition, object gameMapToggle,
        object controlPanelToggle, object switchFloor, object saveMapCache) =>
        _input.ApplyBindings(
            (MapInputBinding)quickScan,
            (MapInputBinding)overlayToggle,
            (MapInputBinding)manualRecognition,
            (MapInputBinding)gameMapToggle,
            (MapInputBinding)controlPanelToggle,
            (MapInputBinding)switchFloor,
            (MapInputBinding)saveMapCache);

    public void ClearBindings() => _input.ClearBindings();
    public void ReleaseAllPressedInputs() => _input.ReleaseAllPressedInputs();
    public void Dispose() => _input.Dispose();
}
