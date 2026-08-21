using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>One pass-through global input binding.</summary>
public sealed class MapInputBinding : IEquatable<MapInputBinding>
{
    public MapInputBindingKind Kind { get; set; }
    public uint VirtualKey { get; set; }
    public MapInputModifiers Modifiers { get; set; }
    public MapMouseButton MouseButton { get; set; }

    [JsonIgnore]
    public bool IsConfigured => Kind != MapInputBindingKind.None;

    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        MapInputBindingKind.Keyboard => FormatKeyboardDisplayName(),
        MapInputBindingKind.Mouse => MouseButton switch
        {
            MapMouseButton.Left => "鼠标左键",
            MapMouseButton.Right => "鼠标右键",
            MapMouseButton.Middle => "鼠标中键",
            MapMouseButton.XButton1 => "鼠标侧键 1",
            MapMouseButton.XButton2 => "鼠标侧键 2",
            _ => "鼠标按键"
        },
        _ => "未设置"
    };

    public MapInputBinding Clone() => new()
    {
        Kind = Kind,
        VirtualKey = VirtualKey,
        Modifiers = Modifiers,
        MouseButton = MouseButton
    };

    public bool Equals(MapInputBinding? other) => other is not null
        && Kind == other.Kind
        && VirtualKey == other.VirtualKey
        && Modifiers == other.Modifiers
        && MouseButton == other.MouseButton;

    public override bool Equals(object? obj) => Equals(obj as MapInputBinding);

    public override int GetHashCode() => HashCode.Combine(Kind, VirtualKey, Modifiers, MouseButton);

    private string FormatKeyboardDisplayName()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(MapInputModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(MapInputModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(MapInputModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(MapInputModifiers.Windows))
            parts.Add("Win");
        parts.Add(((Windows.System.VirtualKey)VirtualKey).ToString());
        return string.Join(" + ", parts);
    }
}
/*
 * 文件职责：MapRuntimeSettings.Input。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
