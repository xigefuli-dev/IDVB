using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>One pass-through global input binding.</summary>
public sealed class MapInputBinding : IEquatable<MapInputBinding>
{
    public MapInputBindingKind Kind { get; set; }
    public uint VirtualKey { get; set; }
    public MapInputModifiers Modifiers { get; set; }
    /// <summary>Non-modifier keys that must remain held before VirtualKey fires.</summary>
    public List<uint> CompanionVirtualKeys { get; set; } = [];
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
        CompanionVirtualKeys = [.. NormalizedCompanionVirtualKeys()],
        MouseButton = MouseButton
    };

    public bool Equals(MapInputBinding? other) => other is not null
        && Kind == other.Kind
        && VirtualKey == other.VirtualKey
        && Modifiers == other.Modifiers
        && NormalizedCompanionVirtualKeys()
            .SequenceEqual(other.NormalizedCompanionVirtualKeys())
        && MouseButton == other.MouseButton;

    public override bool Equals(object? obj) => Equals(obj as MapInputBinding);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind); hash.Add(VirtualKey); hash.Add(Modifiers); hash.Add(MouseButton);
        foreach (var key in NormalizedCompanionVirtualKeys()) hash.Add(key);
        return hash.ToHashCode();
    }

    private string FormatKeyboardDisplayName()
    {
        var parts = new List<string>(5 + (CompanionVirtualKeys?.Count ?? 0));
        if (Modifiers.HasFlag(MapInputModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(MapInputModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(MapInputModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(MapInputModifiers.Windows))
            parts.Add("Win");
        parts.AddRange(NormalizedCompanionVirtualKeys()
            .Select(MapInputKeyDisplayName.FormatVirtualKey));
        parts.Add(MapInputKeyDisplayName.FormatVirtualKey(VirtualKey));
        return string.Join(" + ", parts);
    }

    public IEnumerable<uint> NormalizedCompanionVirtualKeys() =>
        (CompanionVirtualKeys ?? [])
            .Where(key => key != 0 && key <= ushort.MaxValue && key != VirtualKey)
            .Distinct()
            .OrderBy(key => key);
}

/// <summary>地图设置页的 Win32 虚拟键码可读名称。</summary>
internal static class MapInputKeyDisplayName
{
    public static string FormatVirtualKey(uint key)
    {
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)key).ToString();
        if (key is >= 0x60 and <= 0x69)
            return $"小键盘 {key - 0x60}";
        if (key is >= 0x70 and <= 0x87)
            return $"F{key - 0x6F}";

        return key switch
        {
            0x01 => "鼠标左键", 0x02 => "鼠标右键", 0x04 => "鼠标中键",
            0x05 => "鼠标侧键 1", 0x06 => "鼠标侧键 2", 0x08 => "退格键",
            0x09 => "Tab", 0x0C => "Clear", 0x0D => "Enter",
            0x10 or 0xA0 or 0xA1 => "Shift", 0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt", 0x13 => "Pause", 0x14 => "Caps Lock",
            0x15 => "输入法切换", 0x1B => "Esc", 0x20 => "空格",
            0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
            0x25 => "左方向键", 0x26 => "上方向键", 0x27 => "右方向键", 0x28 => "下方向键",
            0x29 => "Select", 0x2A => "Print", 0x2B => "Execute", 0x2C => "Print Screen",
            0x2D => "Insert", 0x2E => "Delete", 0x2F => "Help", 0x5B or 0x5C => "Windows",
            0x5D => "菜单键", 0x6A => "小键盘 *", 0x6B => "小键盘 +",
            0x6C => "小键盘分隔符", 0x6D => "小键盘 -", 0x6E => "小键盘 .", 0x6F => "小键盘 /",
            0x90 => "Num Lock", 0x91 => "Scroll Lock", 0xA6 => "浏览器后退", 0xA7 => "浏览器前进",
            0xA8 => "浏览器刷新", 0xA9 => "浏览器停止", 0xAA => "浏览器搜索", 0xAB => "浏览器收藏夹",
            0xAC => "浏览器主页", 0xAD => "静音", 0xAE => "音量减小", 0xAF => "音量增大",
            0xB0 => "下一曲", 0xB1 => "上一曲", 0xB2 => "停止播放", 0xB3 => "播放/暂停",
            0xB4 => "启动邮件", 0xB5 => "媒体选择", 0xB6 => "启动应用 1", 0xB7 => "启动应用 2",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
            0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            0xDF => "键盘布局按键", 0xE2 => "国际键", 0xE5 => "输入法处理键", 0xE7 => "输入法数据键",
            0xF6 => "Attn", 0xF7 => "CrSel", 0xF8 => "ExSel", 0xF9 => "Erase EOF", 0xFA => "Play",
            0xFB => "Zoom", 0xFC => "系统保留按键", 0xFD => "PA1", 0xFE => "Clear",
            _ => "未命名按键"
        };
    }
}
/*
 * 文件职责：MapRuntimeSettings.Input。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
