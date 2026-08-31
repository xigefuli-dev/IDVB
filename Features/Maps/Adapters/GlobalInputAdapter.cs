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
        _input.RestMapDisplayInvoked += (_, args) => RestMapDisplayInvoked?.Invoke(this, args);
        _input.AltInvoked += (_, args) => AltInvoked?.Invoke(this, args);
        _input.MouseWheelScrolled += (_, args) => MouseWheelScrolled?.Invoke(this, args);
        _input.PluginInputInvoked += (_, args) => PluginInputInvoked?.Invoke(this, args);
    }

    public event EventHandler<object>? QuickScanInvoked;
    public event EventHandler<object>? OverlayToggleInvoked;
    public event EventHandler<object>? ManualRecognitionInvoked;
    public event EventHandler<object>? GameMapToggleInvoked;
    public event EventHandler<object>? ControlPanelToggleInvoked;
    public event EventHandler<object>? SwitchFloorInvoked;
    public event EventHandler<object>? SaveMapCacheInvoked;
    public event EventHandler<object>? RestMapDisplayInvoked;
    public event EventHandler<object>? AltInvoked;
    public event EventHandler<MouseWheelInputEventArgs>? MouseWheelScrolled;
    public event EventHandler<PluginInputInvokedEventArgs>? PluginInputInvoked;

    public void ApplyBindings(object quickScan, object overlayToggle,
        object manualRecognition, object gameMapToggle,
        object controlPanelToggle, object switchFloor, object saveMapCache,
        object restMapDisplay) =>
        _input.ApplyBindings(
            (MapInputBinding)quickScan,
            (MapInputBinding)overlayToggle,
            (MapInputBinding)manualRecognition,
            (MapInputBinding)gameMapToggle,
            (MapInputBinding)controlPanelToggle,
            (MapInputBinding)switchFloor,
            (MapInputBinding)saveMapCache,
            (MapInputBinding)restMapDisplay);

    public void ClearBindings() => _input.ClearBindings();

    public void ApplyPluginBinding(string pluginId, string bindingKey, object binding) =>
        _input.ApplyPluginBinding(
            pluginId,
            bindingKey,
            ToMapBinding(binding));

    public void ClearPluginBindings(string pluginId) =>
        _input.ClearPluginBindings(pluginId);

    public bool IsPluginBindingPressed(string pluginId, string bindingKey) =>
        _input.IsPluginBindingPressed(pluginId, bindingKey);

    public void ReleaseAllPressedInputs() => _input.ReleaseAllPressedInputs();
    public void Dispose() => _input.Dispose();

    private static MapInputBinding ToMapBinding(object binding)
    {
        if (binding is MapInputBinding mapBinding)
            return mapBinding;

        // PluginContracts is intentionally not referenced by the shared RealCLI
        // source graph. Convert the SDK's stable, primitive-shaped binding at
        // this boundary instead of leaking the GUI/plugin assembly into Core.
        var type = binding.GetType();
        return new MapInputBinding
        {
            Kind = (MapInputBindingKind)ReadInt(type, binding, "Kind"),
            VirtualKey = (uint)ReadInt(type, binding, "VirtualKey"),
            Modifiers = (MapInputModifiers)ReadInt(type, binding, "Modifiers"),
            CompanionVirtualKeys = ReadUIntList(type, binding, "CompanionVirtualKeys"),
            MouseButton = (MapMouseButton)ReadInt(type, binding, "MouseButton")
        };
    }

    private static int ReadInt(Type type, object instance, string propertyName)
    {
        var property = type.GetProperty(propertyName)
            ?? throw new ArgumentException(
                $"Plugin input binding is missing '{propertyName}'.", nameof(instance));
        var value = property.GetValue(instance)
            ?? throw new ArgumentException(
                $"Plugin input binding property '{propertyName}' is null.", nameof(instance));
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<uint> ReadUIntList(Type type, object instance, string propertyName)
    {
        var value = type.GetProperty(propertyName)?.GetValue(instance);
        return value is System.Collections.IEnumerable values
            ? values.Cast<object>().Select(Convert.ToUInt32).ToList()
            : [];
    }
}
/*
 * 文件职责：GlobalInputAdapter。
 * 所属模块：Features/Maps，主要负责地图功能与基础设施之间的适配边界。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
