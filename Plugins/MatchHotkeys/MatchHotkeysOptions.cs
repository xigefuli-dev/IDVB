using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.MatchHotkeys;

/// <summary>
/// 「对局快捷键」的设置键、默认值与取值规则。
///
/// 两个快捷键各自绑定一个地图模式（宿主里的「地图 Class / 模式」，例如
/// 「S0 厄运之女 · 困难」）：按一下进入该模式的对局，再按一下结束。
/// 快捷键默认「未设置」，由用户在插件设置页里自行录制。
/// </summary>
internal sealed record MatchHotkeysOptions
{
    /// <summary>插件 Id（与 <c>[Plugin]</c> 特性、注册表、偏好文件里的键一致）。</summary>
    public const string PluginId = "match-hotkeys";

    public const string HotkeyAKey = "hotkey-map-a";
    public const string HotkeyBKey = "hotkey-map-b";
    public const string MapClassAKey = "map-class-a";
    public const string MapClassBKey = "map-class-b";
    public const string ForegroundOnlyKey = "foreground-only";

    /// <summary>设置页里「按键」折叠区的标题（两个快捷键收在这里，默认收起）。</summary>
    public const string KeyGroupTitle = "按键";

    /// <summary>设置页里「目标」折叠区的标题（两张地图收在这里，默认收起）。</summary>
    public const string TargetGroupTitle = "目标";

    /// <summary>「未设置」的稳定存储串，与宿主 <see cref="PluginInputBinding"/> 的约定一致。</summary>
    public const string NoBindingStorageValue = "none";

    /// <summary>
    /// 下拉框第一个选项，表示「还没选地图模式」。它永远排在候选列表最前面并作为默认项，
    /// 这样全新安装时不会把地图库里的第一张图当成默认目标；它也不会被当作有效地图模式。
    /// </summary>
    public const string UnsetClassOption = "（请选择地图模式）";

    /// <summary>两个快捷键都不绑定、两个地图都未指定、仅游戏内生效的出厂状态。</summary>
    public static MatchHotkeysOptions Default { get; } = new();

    public PluginInputBinding HotkeyA { get; init; } = new();

    public PluginInputBinding HotkeyB { get; init; } = new();

    public string MapClassA { get; init; } = string.Empty;

    public string MapClassB { get; init; } = string.Empty;

    /// <summary>仅当前台窗口属于游戏（dwrg.exe）时才动作，避免在别的程序里误触发。</summary>
    public bool ForegroundOnly { get; init; } = true;

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
