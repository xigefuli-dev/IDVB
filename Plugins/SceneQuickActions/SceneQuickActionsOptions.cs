using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 运行时参数快照。设置页改动后整体替换一份新实例，工作线程只在循环开头读一次，
/// 因此磁盘/UI 线程与工作线程之间不需要细粒度锁。
///
/// 「切出鼠标」的行为是固定的：按一次 Tab 打开背包把系统指针切出来，动作做完再按
/// 一次 Tab 关掉背包。旧版的「切出鼠标方式」下拉框与「切出鼠标按键」都已按用户要求
/// 删除——实测合成的鼠标侧键游戏不认，而 Tab 这类游戏内键位走得通。
/// </summary>
public sealed record SceneQuickActionsOptions
{
    /// <summary>设置页里「配置按键」可折叠区的标题。</summary>
    public const string KeyGroupTitle = "配置按键";

    /// <summary>设置页里「间隔设置」可折叠区的标题。</summary>
    public const string TimingGroupTitle = "间隔设置";

    /// <summary>「切出鼠标」固定用的键：Tab（打开 / 关闭背包）。</summary>
    public const uint WakeVirtualKey = 0x09;

    /// <summary>「丢光背包」热键默认值：F5。</summary>
    public const uint DefaultDropBagVirtualKey = 0x74;

    /// <summary>「逐个丢道具栏」热键默认值：F6。</summary>
    public const uint DefaultDropHotbarVirtualKey = 0x75;

    public const string DropBagHotkeyKey = "drop-bag-hotkey";
    public const string DropHotbarHotkeyKey = "drop-hotbar-hotkey";
    public const string DropSilenceKey = "drop-silence-seconds";
    public const string DropSpeedKey = "drop-speed";

    /// <summary>拖拽速度档位文案。</summary>
    public static IReadOnlyList<string> DropSpeedOptions { get; } = ["快", "标准", "慢（稳妥）"];

    /// <summary>拖拽各段时长（毫秒）：整条轨迹时长 / 到位后停顿 / 每格之间间隔。</summary>
    public static (int Move, int Settle, int Interval) GetDropTiming(int speedIndex) =>
        speedIndex switch
        {
            0 => (150, 60, 180),
            1 => (260, 90, 300),
            _ => (400, 150, 600)
        };

    /// <summary>把设置页存下来的档位文案还原成下标；不认得的回退到「快」。</summary>
    public static int ParseDropSpeed(string? option)
    {
        var index = DropSpeedOptions
            .ToList()
            .FindIndex(candidate => string.Equals(candidate, option, StringComparison.Ordinal));
        return index < 0 ? 0 : index;
    }

    public static string ToDropSpeedOption(int speedIndex) =>
        DropSpeedOptions[Math.Clamp(speedIndex, 0, DropSpeedOptions.Count - 1)];

    /// <summary>触发丢东西后暂停「拾取相关」检测的秒数范围。</summary>
    public const int DropSilenceMinimum = 5;
    public const int DropSilenceMaximum = 120;

    public const string InviteEnabledKey = "invite-enabled";
    public const string WakeDelayKey = "wake-delay-ms";
    public const string PickupEnabledKey = "pickup-enabled";
    public const string PickupAllBindingKey = "pickup-all-binding";
    public const string QuickReplaceBindingKey = "quick-replace-binding";
    public const string BetweenClicksKey = "between-clicks-ms";
    public const string PickupWakeKey = "pickup-wake-mouse";
    public const string PollIntervalKey = "poll-interval-ms";
    public const string ThresholdKey = "match-threshold-percent";
    public const string CooldownKey = "cooldown-ms";

    /// <summary>插件绑定存储层里表示「未设置」的稳定字符串。</summary>
    public const string NoBindingStorageValue = "none";

    /// <summary>「拾取全部」的游戏默认按键（B）。</summary>
    public const uint DefaultPickupAllVirtualKey = 0x42;

    public const int WakeDelayMinimum = 40;
    public const int WakeDelayMaximum = 600;
    public const int BetweenClicksMinimum = 60;
    public const int BetweenClicksMaximum = 1200;
    public const int PollIntervalMinimum = 200;
    public const int PollIntervalMaximum = 2000;
    public const int ThresholdMinimum = 60;
    public const int ThresholdMaximum = 95;
    public const int CooldownMinimum = 300;
    public const int CooldownMaximum = 10000;

    /// <summary>自动接受邀请：识别到「邀请你牵手」的接受按钮时自动点「接受」。</summary>
    public bool InviteEnabled { get; init; } = true;

    /// <summary>切出鼠标后、移动指针前的等待时间。</summary>
    public int WakeDelayMilliseconds { get; init; } = 150;

    /// <summary>拾取相关：识别到「可拾取」面板时自动「拾取全部」→「快捷替换」。</summary>
    public bool PickupEnabled { get; init; } = true;

    /// <summary>「拾取全部」用的按键；未设置（none）时改为鼠标点击该提示。</summary>
    public PluginInputBinding PickupAllBinding { get; init; } =
        PluginInputBinding.Keyboard(DefaultPickupAllVirtualKey);

    /// <summary>「快捷替换」用的按键；未设置（none，默认）时改为鼠标点击该按钮。</summary>
    public PluginInputBinding QuickReplaceBinding { get; init; } = new();

    /// <summary>「拾取全部」与「快捷替换」两次动作之间的间隔。</summary>
    public int BetweenClicksMilliseconds { get; init; } = 260;

    /// <summary>拾取相关是否也需要先切出鼠标（只有用鼠标点击时才需要）。</summary>
    public bool PickupWakeMouse { get; init; }

    /// <summary>两次检测之间的间隔。</summary>
    public int PollIntervalMilliseconds { get; init; } = 400;

    /// <summary>模板匹配相似度阈值（百分比）。</summary>
    public int MatchThresholdPercent { get; init; } = 80;

    /// <summary>同一场景两次动作之间的最小间隔。</summary>
    public int CooldownMilliseconds { get; init; } = 1500;

    /// <summary>「丢光背包」热键（默认 F5；可录制成键盘键或鼠标键）。</summary>
    public PluginInputBinding DropBagHotkey { get; init; } =
        PluginInputBinding.Keyboard(DefaultDropBagVirtualKey);

    /// <summary>「逐个丢道具栏」热键（默认 F6；可录制成键盘键或鼠标键）。</summary>
    public PluginInputBinding DropHotbarHotkey { get; init; } =
        PluginInputBinding.Keyboard(DefaultDropHotbarVirtualKey);

    /// <summary>触发丢东西后暂停「拾取相关」的秒数。</summary>
    public int DropSilenceSeconds { get; init; } = 30;

    /// <summary>拖拽速度档位下标（0 快 / 1 标准 / 2 慢）。</summary>
    public int DropSpeedIndex { get; init; }

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
}
