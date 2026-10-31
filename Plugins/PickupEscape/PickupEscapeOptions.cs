using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.PickupEscape;

/// <summary>
/// 运行时参数快照。设置页改动后整体替换一份新实例，工作线程只在循环开头读一次，
/// 因此磁盘/UI 线程与工作线程之间不需要细粒度锁。
/// </summary>
public sealed record PickupEscapeOptions
{
    public const string PluginId = "pickup-escape";

    /// <summary>设置页里「动作」可折叠区的标题。</summary>
    public const string ActionGroupTitle = "动作";

    /// <summary>设置页里「间隔设置」可折叠区的标题。</summary>
    public const string TimingGroupTitle = "间隔设置";

    public const string ToggleHotkeyKey = "toggle-hotkey";
    public const string PickupListBindingKey = "pickup-list-binding";
    public const string PauseAfterPickupListKey = "pause-after-pickup-list-seconds";
    public const string MinimumVisibleKey = "minimum-visible-ms";
    public const string EscapeSettingsKey = "escape-settings-enabled";
    public const string ActionBindingKey = "action-binding";
    public const string PollIntervalKey = "poll-interval-ms";
    public const string ThresholdKey = "match-threshold-percent";
    public const string CooldownKey = "cooldown-ms";
    public const string MinimumRandomDelayKey = "minimum-random-delay-ms";
    public const string MaximumRandomDelayKey = "maximum-random-delay-ms";

    /// <summary>插件绑定存储层里表示「未设置」的稳定字符串。</summary>
    public const string NoBindingStorageValue = "none";

    /// <summary>
    /// 「识别到面板时按下的键」默认值：Esc（VK 0x1B）。
    /// 游戏里「可拾取」面板标题栏右侧写的就是它，与面板右上角那个 ✕ 等价。
    /// </summary>
    public const uint DefaultActionVirtualKey = 0x1B;

    /// <summary>
    /// 「展开拾取列表」的默认按键：H（VK 0x48）。
    /// 游戏里按它会把拾取列表展开出来；按下之后的一段时间内本插件不按 ESC。
    /// </summary>
    public const uint DefaultPickupListVirtualKey = 0x48;

    public const int PollIntervalMinimum = 200;
    public const int PollIntervalMaximum = 2000;
    public const int ThresholdMinimum = 60;
    public const int ThresholdMaximum = 95;
    public const int CooldownMinimum = 300;
    public const int CooldownMaximum = 10000;
    public const int MaximumRandomDelayMillisecondsAllowed = 10000;
    public const int PauseSecondsMinimum = 1;
    public const int PauseSecondsMaximum = 60;
    public const int MinimumVisibleMinimum = 0;
    public const int MinimumVisibleMaximum = 2000;

    /// <summary>两次检测之间的间隔。</summary>
    public int PollIntervalMilliseconds { get; init; } = 400;

    /// <summary>模板匹配相似度阈值（百分比）。</summary>
    public int MatchThresholdPercent { get; init; } = 80;

    /// <summary>同一块「可拾取」面板两次动作之间的最小间隔。</summary>
    public int CooldownMilliseconds { get; init; } = 1500;

    public int MinimumRandomDelayMilliseconds { get; init; } = 30;

    public int MaximumRandomDelayMilliseconds { get; init; } = 50;

    /// <summary>切换「检测运行 / 暂停」的热键；默认「未设置」，需要用户在设置页自己录。</summary>
    public PluginInputBinding ToggleHotkey { get; init; } = new();

    /// <summary>
    /// 游戏里「展开拾取列表」的按键（默认 H）：按下它之后的若干秒内不按 ESC，
    /// 免得把你刚手动展开的列表立刻关掉。
    /// </summary>
    public PluginInputBinding PickupListBinding { get; init; } =
        PluginInputBinding.Keyboard(DefaultPickupListVirtualKey);

    /// <summary>按下「展开拾取列表」后暂停「按 ESC」的秒数。</summary>
    public int PauseAfterPickupListSeconds { get; init; } = 10;

    /// <summary>
    /// 「可拾取」面板要**连续出现**这么久（毫秒）才按 ESC —— 一闪而过的不算，
    /// 免得人已经走开、ESC 没东西可关时反而把游戏设置界面开出来。0 = 不设门槛（一认出就按）。
    /// </summary>
    public int MinimumVisibleMilliseconds { get; init; } = 250;

    /// <summary>
    /// 识别到误开的游戏设置界面时，是否再补按一下 ESC 退回上一界面。
    /// 面板稳定门槛是「第一道防线」，这一项是「万一还是开出来了」的补救。
    /// </summary>
    public bool EscapeSettingsEnabled { get; init; } = true;

    /// <summary>识别到面板时要按下的键；默认 Esc。</summary>
    public PluginInputBinding ActionBinding { get; init; } =
        PluginInputBinding.Keyboard(DefaultActionVirtualKey);

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var lower = Math.Clamp(MinimumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(30), MaximumRandomDelayMillisecondsAllowed);
        var upper = Math.Clamp(MaximumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(50), MaximumRandomDelayMillisecondsAllowed);
        return (Math.Min(lower, upper), Math.Max(lower, upper));
    }

    /// <summary>每段动作后保留原有等待，再独立追加一次随机延迟。</summary>
    public int GetDelayAfterStep(int baseMilliseconds = 0)
    {
        var (minimum, maximum) = GetOrderedRandomDelayRange();
        return Math.Clamp(baseMilliseconds, 0, 10000) + Random.Shared.Next(minimum, maximum + 1);
    }

    public double MatchThreshold =>
        Clamp(MatchThresholdPercent, ThresholdMinimum, ThresholdMaximum) / 100d;

    public static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    public static int FromDouble(object? value, int fallback, int minimum, int maximum)
    {
        var parsed = value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            _ => double.NaN
        };
        return double.IsFinite(parsed)
            ? Clamp((int)Math.Round(parsed), minimum, maximum)
            : fallback;
    }

    /// <summary>把一份绑定收敛成「单一按键」的热键：带修饰键或伴随键的一律不采用。</summary>
    public static bool TryAsHotkey(PluginInputBinding? binding, out PluginInputBinding hotkey)
    {
        hotkey = new PluginInputBinding();
        if (binding is null
            || !binding.IsConfigured
            || binding.Modifiers != PluginInputModifiers.None
            || (binding.CompanionVirtualKeys?.Count ?? 0) > 0)
        {
            return false;
        }

        hotkey = binding;
        return true;
    }
}
