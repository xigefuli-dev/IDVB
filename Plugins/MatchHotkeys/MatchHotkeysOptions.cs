using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.MatchHotkeys;

/// <summary>
/// 「对局快捷键」的设置键、默认值与取值规则。
///
/// 五个快捷键各自绑定一个地图模式（宿主里的「地图 Class / 模式」，例如
/// 「S0 厄运之女 · 困难」）：按一下进入该模式的对局，再按一下结束。
/// 快捷键默认「未设置」，由用户在插件设置页里自行录制；用不到的槽位保持
/// 「未设置」/「（请选择地图模式）」，按下不会有任何动作。
///
/// 槽位与存储键的对应关系集中在下面的四张表里：加槽位只需要把
/// <see cref="SlotCount"/> 与几张表一起加长，插件主体与设置页都是按表循环生成的。
/// </summary>
internal sealed record MatchHotkeysOptions
{
    /// <summary>插件 Id（与 <c>[Plugin]</c> 特性、注册表、偏好文件里的键一致）。</summary>
    public const string PluginId = "match-hotkeys";

    /// <summary>可用的快捷键槽位数（快捷键一 … 快捷键五）。</summary>
    public const int SlotCount = 5;

    public const string HotkeyAKey = "hotkey-map-a";
    public const string HotkeyBKey = "hotkey-map-b";
    public const string HotkeyCKey = "hotkey-map-c";
    public const string HotkeyDKey = "hotkey-map-d";
    public const string HotkeyEKey = "hotkey-map-e";
    public const string MapClassAKey = "map-class-a";
    public const string MapClassBKey = "map-class-b";
    public const string MapClassCKey = "map-class-c";
    public const string MapClassDKey = "map-class-d";
    public const string MapClassEKey = "map-class-e";
    public const string ForegroundOnlyKey = "foreground-only";

    /// <summary>设置页里「按键」折叠区的标题（各快捷键收在这里，默认收起）。</summary>
    public const string KeyGroupTitle = "按键";

    /// <summary>设置页里「目标」折叠区的标题（各目标地图收在这里，默认收起）。</summary>
    public const string TargetGroupTitle = "目标";

    /// <summary>「未设置」的稳定存储串，与宿主 <see cref="PluginInputBinding"/> 的约定一致。</summary>
    public const string NoBindingStorageValue = "none";

    /// <summary>
    /// 下拉框第一个选项，表示「还没选地图模式」。它永远排在候选列表最前面并作为默认项，
    /// 这样全新安装时不会把地图库里的第一张图当成默认目标；它也不会被当作有效地图模式。
    /// </summary>
    public const string UnsetClassOption = "（请选择地图模式）";

    private static readonly string[] HotkeyKeys =
        [HotkeyAKey, HotkeyBKey, HotkeyCKey, HotkeyDKey, HotkeyEKey];

    private static readonly string[] MapClassKeys =
        [MapClassAKey, MapClassBKey, MapClassCKey, MapClassDKey, MapClassEKey];

    private static readonly string[] SlotNames =
        ["快捷键一", "快捷键二", "快捷键三", "快捷键四", "快捷键五"];

    private static readonly string[] TargetNames =
        ["地图 A", "地图 B", "地图 C", "地图 D", "地图 E"];

    /// <summary>所有槽位的快捷键存储键（日志与探针按此遍历）。</summary>
    public static IReadOnlyList<string> HotkeyStorageKeys => HotkeyKeys;

    /// <summary>所有槽位的地图模式存储键。</summary>
    public static IReadOnlyList<string> MapClassStorageKeys => MapClassKeys;

    /// <summary>槽位（0 起）→ 快捷键存储键。</summary>
    public static string HotkeyKeyAt(int slot) => HotkeyKeys[ClampSlot(slot)];

    /// <summary>槽位（0 起）→ 地图模式存储键。</summary>
    public static string MapClassKeyAt(int slot) => MapClassKeys[ClampSlot(slot)];

    /// <summary>槽位（0 起）→ 设置页显示名（「快捷键一」…）。</summary>
    public static string SlotNameAt(int slot) => SlotNames[ClampSlot(slot)];

    /// <summary>槽位（0 起）→ 设置页目标名（「地图 A」…）。</summary>
    public static string TargetNameAt(int slot) => TargetNames[ClampSlot(slot)];

    /// <summary>快捷键存储键 → 槽位；不是本插件的快捷键键时返回 -1。</summary>
    public static int SlotOfHotkeyKey(string? key) =>
        Array.FindIndex(HotkeyKeys, candidate =>
            string.Equals(candidate, key, StringComparison.Ordinal));

    /// <summary>地图模式存储键 → 槽位；不是本插件的目标键时返回 -1。</summary>
    public static int SlotOfMapClassKey(string? key) =>
        Array.FindIndex(MapClassKeys, candidate =>
            string.Equals(candidate, key, StringComparison.Ordinal));

    private static int ClampSlot(int slot) => Math.Clamp(slot, 0, SlotCount - 1);

    /// <summary>五个快捷键都不绑定、五张地图都未指定、仅游戏内生效的出厂状态。</summary>
    public static MatchHotkeysOptions Default { get; } = new();

    public PluginInputBinding HotkeyA { get; init; } = new();

    public PluginInputBinding HotkeyB { get; init; } = new();

    public PluginInputBinding HotkeyC { get; init; } = new();

    public PluginInputBinding HotkeyD { get; init; } = new();

    public PluginInputBinding HotkeyE { get; init; } = new();

    public string MapClassA { get; init; } = string.Empty;

    public string MapClassB { get; init; } = string.Empty;

    public string MapClassC { get; init; } = string.Empty;

    public string MapClassD { get; init; } = string.Empty;

    public string MapClassE { get; init; } = string.Empty;

    /// <summary>仅当前台窗口属于游戏（dwrg.exe）时才动作，避免在别的程序里误触发。</summary>
    public bool ForegroundOnly { get; init; } = true;

    /// <summary>读某个槽位的快捷键（槽位越界时夹到范围内，绝不抛异常）。</summary>
    public PluginInputBinding HotkeyAt(int slot) => ClampSlot(slot) switch
    {
        0 => HotkeyA,
        1 => HotkeyB,
        2 => HotkeyC,
        3 => HotkeyD,
        _ => HotkeyE
    };

    /// <summary>读某个槽位绑定的地图模式（槽位越界时夹到范围内）。</summary>
    public string MapClassAt(int slot) => ClampSlot(slot) switch
    {
        0 => MapClassA,
        1 => MapClassB,
        2 => MapClassC,
        3 => MapClassD,
        _ => MapClassE
    };

    /// <summary>写入某个槽位的快捷键，返回新的设置实例（record 不可变）。</summary>
    public MatchHotkeysOptions WithHotkey(int slot, PluginInputBinding binding) => ClampSlot(slot) switch
    {
        0 => this with { HotkeyA = binding },
        1 => this with { HotkeyB = binding },
        2 => this with { HotkeyC = binding },
        3 => this with { HotkeyD = binding },
        _ => this with { HotkeyE = binding }
    };

    /// <summary>写入某个槽位的地图模式，返回新的设置实例。</summary>
    public MatchHotkeysOptions WithMapClass(int slot, string mapClass) => ClampSlot(slot) switch
    {
        0 => this with { MapClassA = mapClass },
        1 => this with { MapClassB = mapClass },
        2 => this with { MapClassC = mapClass },
        3 => this with { MapClassD = mapClass },
        _ => this with { MapClassE = mapClass }
    };

    /// <summary>「未选择」项与空白串都视为「还没选地图模式」。</summary>
    public static bool IsUnsetClass(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value, UnsetClassOption, StringComparison.Ordinal);

    /// <summary>
    /// 解析设置页录制的绑定串。键盘键、鼠标键、组合键都由宿主输入通道支持；
    /// <c>none</c> 也是合法值，表示「未设置 / 已清空」。
    /// </summary>
    public static bool TryParseHotkey(string? value, out PluginInputBinding binding) =>
        PluginInputBinding.TryParse(value, PluginInputBindingKinds.All, out binding);
}
