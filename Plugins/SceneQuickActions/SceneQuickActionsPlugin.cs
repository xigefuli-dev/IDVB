using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 背包辅助：把《第五人格》里两个反复出现的界面操作自动化。
///
/// 自动接受邀请 —— 有人邀请你牵手（右上角出现「××邀请你牵手」+「拒绝 / 接受」）：
///   切出鼠标 → 移到「接受」→ 左键点击。
/// 拾取相关 —— 出现「可拾取」面板（右上角列出物品，下方有「拾取全部」提示）：
///   先「拾取全部」→ 再「快捷替换」；这两步各自可以用按键（可配置）或鼠标点击完成。
///
/// 识别方式是对宿主抓到的客户区画面做多尺度模板匹配，模板来自用户提供的
/// 1920×1080 游戏截图；命中后才注入输入，注入前会再次确认游戏仍在前台。
/// </summary>
[Plugin(
    "scene-quick-actions",
    DisplayName = "背包辅助",
    Description = "自动识别「邀请你牵手」与「可拾取」两个界面，执行接受邀请、拾取全部、快捷替换。",
    Version = "1.7.0",
    AlwaysActive = true)]
public sealed class SceneQuickActionsPlugin
    : PluginBase, IPluginSettingsProvider, IHandle<SessionStateChangedMessage>
{
    private SceneQuickActionsMatcher? _matcher;
    private SceneQuickActionsRunner? _runner;
    private SceneQuickActionsOptions _options = new();
    private bool _enabled;

    public override string Id => "scene-quick-actions";

    public override string DisplayName => "背包辅助";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginToggleSetting
        {
            Key = SceneQuickActionsOptions.InviteEnabledKey,
            DisplayName = "自动接受邀请",
            Description = "识别到「接受」按钮时，自动切出鼠标并点击它。",
            DefaultValue = true
        },
        new PluginToggleSetting
        {
            Key = SceneQuickActionsOptions.PickupEnabledKey,
            DisplayName = "拾取相关",
            Description = "识别到「可拾取」面板时，先「拾取全部」，再「快捷替换」。",
            DefaultValue = true
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.DropBagHotkeyKey,
            DisplayName = "丢光背包 · 热键（键盘键或鼠标键）",
            Description = "按下后先按 Tab 开背包切出鼠标，再把上排 3 格、中排 3 格依次拖到屏幕正中心松手，最后按 Tab 关背包。点右边按钮即可录制新键；不支持组合键。",
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultDropBagVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.DropHotbarHotkeyKey,
            DisplayName = "逐个丢道具栏 · 热键（键盘键或鼠标键）",
            Description = "每按一次，先切出鼠标，再把底部道具栏的下一个格子拖到屏幕正中心松手：第 1 次拖第 1 格，第 2 次拖第 2 格，依次循环。点右边按钮即可录制新键；不支持组合键。",
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultDropHotbarVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.PickupAllBindingKey,
            DisplayName = "拾取全部按键",
            Description = "游戏里「拾取全部」提示上显示的按键（默认 B 键）。点「重置按键」清空后，改为用鼠标点击该提示。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultPickupAllVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.QuickReplaceBindingKey,
            DisplayName = "快捷替换按键",
            Description = "若你的游戏里「快捷替换」也有按键提示，就在这里录一个；保持「未设置」时用鼠标点击该按钮。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = SceneQuickActionsOptions.NoBindingStorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginToggleSetting
        {
            Key = SceneQuickActionsOptions.PickupWakeKey,
            DisplayName = "拾取相关动作前先切出鼠标",
            Description = "两步都用按键时不需要。打开后同样用「按 Tab 开背包」切出指针，动作完自动按 Tab 关上。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = false
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.WakeDelayKey,
            DisplayName = "切出鼠标后等待（毫秒）",
            Description = "按 Tab 打开背包后，等指针真正出现再移动点击（内部下限 120ms，避免背包还没开就开始点）。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.WakeDelayMinimum,
            Maximum = SceneQuickActionsOptions.WakeDelayMaximum,
            StepFrequency = 10,
            DefaultValue = 150
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.BetweenClicksKey,
            DisplayName = "两次动作间隔（毫秒）",
            Description = "「拾取全部」与「快捷替换」之间的等待时间。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.BetweenClicksMinimum,
            Maximum = SceneQuickActionsOptions.BetweenClicksMaximum,
            StepFrequency = 20,
            DefaultValue = 260
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.PollIntervalKey,
            DisplayName = "检测间隔（毫秒）",
            Description = "多久抓一帧做识别；只在游戏处于前台时才会真正抓帧。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.PollIntervalMinimum,
            Maximum = SceneQuickActionsOptions.PollIntervalMaximum,
            StepFrequency = 50,
            DefaultValue = 400
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.ThresholdKey,
            DisplayName = "识别相似度阈值（%）",
            Description = "越高越不容易误判，但界面被遮挡或分辨率差异大时可能识别不到。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.ThresholdMinimum,
            Maximum = SceneQuickActionsOptions.ThresholdMaximum,
            StepFrequency = 1,
            DefaultValue = 80
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.CooldownKey,
            DisplayName = "同一场景动作冷却（毫秒）",
            Description = "同一场景两次动作之间的最小间隔，避免连续重复点击。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.CooldownMinimum,
            Maximum = SceneQuickActionsOptions.CooldownMaximum,
            StepFrequency = 100,
            DefaultValue = 1500
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.DropSilenceKey,
            DisplayName = "丢东西后暂停拾取（秒）",
            Description = "按上面两个热键之后，这段时间内「拾取相关」不再动作，免得把自己刚丢出来的东西又捡回去。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.DropSilenceMinimum,
            Maximum = SceneQuickActionsOptions.DropSilenceMaximum,
            StepFrequency = 5,
            DefaultValue = 30
        },
        new PluginChoiceSetting
        {
            Key = SceneQuickActionsOptions.DropSpeedKey,
            DisplayName = "拖拽速度",
            Description = "两个热键拖动时的快慢。嫌慢选「快」，如果丢不出去（拖拽太快游戏没吃到）就换「慢（稳妥）」。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Options = [.. SceneQuickActionsOptions.DropSpeedOptions],
            DefaultIndex = 0
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);

        try
        {
            _matcher = new SceneQuickActionsMatcher();
        }
        catch (Exception exception)
        {
            context.Logger.Error($"背包辅助加载识别模板失败，插件不可用：{exception.Message}");
            return;
        }

        var screenshot = context.GetService<IPluginScreenshotService>();
        var gameWindow = context.GetService<IPluginGameWindowService>();
        if (screenshot is null && gameWindow is null)
        {
            context.Logger.Error("宿主没有提供截图能力，背包辅助不可用。");
            return;
        }

        var grabber = new GameFrameGrabber(
            context,
            screenshot,
            gameWindow,
            message => context.Logger.Info(message));
        _runner = new SceneQuickActionsRunner(grabber, _matcher, context.Logger);
        _runner.AttachHotkeys(
            context.GetService<IPluginInputService>(),
            Id);
        _runner.SetOptions(_options);
    }

    public override void OnEnable()
    {
        _enabled = true;
        if (_runner is null)
        {
            Context.Logger.Error("背包辅助未成功初始化，无法启动检测。");
            return;
        }

        _runner.SetOptions(_options);
        _runner.Start();
        Context.Logger.Info(
            $"参数：检测间隔 {_options.PollIntervalMilliseconds}ms，阈值 {_options.MatchThresholdPercent}%，"
            + $"自动接受邀请 {(_options.InviteEnabled ? "开" : "关")}，拾取相关 {(_options.PickupEnabled ? "开" : "关")}；"
            + "切出鼠标 按 Tab 开背包（动作完再按 Tab 关上）；"
            + $"拾取全部 {_options.PickupAllBinding.DisplayName}，"
            + $"快捷替换 {_options.QuickReplaceBinding.DisplayName}。");
    }

    public override void OnDisable()
    {
        _enabled = false;
        _runner?.Stop();
    }

    public override void OnUnload()
    {
        _enabled = false;
        _runner?.Dispose();
        _runner = null;
        _matcher?.Dispose();
        _matcher = null;
    }

    /// <summary>热键键位在设置页改动后，重新注册到宿主输入通道。</summary>
    public void RefreshHotkeys() => _runner?.RefreshHotkeyBindings();

    public void Handle(SessionStateChangedMessage message) =>
        _runner?.SetMapOpen(message.GameMapOpen);

    public object? GetSettingValue(string key) => key switch
    {
        SceneQuickActionsOptions.InviteEnabledKey => _options.InviteEnabled,
        SceneQuickActionsOptions.WakeDelayKey => (double)_options.WakeDelayMilliseconds,
        SceneQuickActionsOptions.PickupEnabledKey => _options.PickupEnabled,
        SceneQuickActionsOptions.PickupAllBindingKey => _options.PickupAllBinding.StorageValue,
        SceneQuickActionsOptions.QuickReplaceBindingKey => _options.QuickReplaceBinding.StorageValue,
        SceneQuickActionsOptions.BetweenClicksKey => (double)_options.BetweenClicksMilliseconds,
        SceneQuickActionsOptions.PickupWakeKey => _options.PickupWakeMouse,
        SceneQuickActionsOptions.PollIntervalKey => (double)_options.PollIntervalMilliseconds,
        SceneQuickActionsOptions.ThresholdKey => (double)_options.MatchThresholdPercent,
        SceneQuickActionsOptions.CooldownKey => (double)_options.CooldownMilliseconds,
        SceneQuickActionsOptions.DropBagHotkeyKey => _options.DropBagHotkey.StorageValue,
        SceneQuickActionsOptions.DropHotbarHotkeyKey => _options.DropHotbarHotkey.StorageValue,
        SceneQuickActionsOptions.DropSilenceKey => (double)_options.DropSilenceSeconds,
        SceneQuickActionsOptions.DropSpeedKey =>
            SceneQuickActionsOptions.ToDropSpeedOption(_options.DropSpeedIndex),
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        var updated = BuildOptions(key, value);
        if (ReferenceEquals(updated, _options))
            return;

        _options = updated;
        if (_enabled)
        {
            _runner?.SetOptions(_options);
            _runner?.RefreshHotkeyBindings();
        }
    }

    private SceneQuickActionsOptions BuildOptions(string key, object? value)
    {
        switch (key)
        {
            case SceneQuickActionsOptions.InviteEnabledKey when value is bool inviteEnabled:
                return _options with { InviteEnabled = inviteEnabled };
            case SceneQuickActionsOptions.WakeDelayKey:
                return _options with
                {
                    WakeDelayMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.WakeDelayMilliseconds,
                        SceneQuickActionsOptions.WakeDelayMinimum,
                        SceneQuickActionsOptions.WakeDelayMaximum)
                };
            case SceneQuickActionsOptions.PickupEnabledKey when value is bool pickupEnabled:
                return _options with { PickupEnabled = pickupEnabled };
            case SceneQuickActionsOptions.PickupAllBindingKey when value is string pickupBinding:
                return TryParseBinding(pickupBinding, out var parsedPickup)
                    ? _options with { PickupAllBinding = parsedPickup }
                    : _options;
            case SceneQuickActionsOptions.QuickReplaceBindingKey when value is string replaceBinding:
                return TryParseBinding(replaceBinding, out var parsedReplace)
                    ? _options with { QuickReplaceBinding = parsedReplace }
                    : _options;
            case SceneQuickActionsOptions.BetweenClicksKey:
                return _options with
                {
                    BetweenClicksMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.BetweenClicksMilliseconds,
                        SceneQuickActionsOptions.BetweenClicksMinimum,
                        SceneQuickActionsOptions.BetweenClicksMaximum)
                };
            case SceneQuickActionsOptions.PickupWakeKey when value is bool pickupWake:
                return _options with { PickupWakeMouse = pickupWake };
            case SceneQuickActionsOptions.PollIntervalKey:
                return _options with
                {
                    PollIntervalMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.PollIntervalMilliseconds,
                        SceneQuickActionsOptions.PollIntervalMinimum,
                        SceneQuickActionsOptions.PollIntervalMaximum)
                };
            case SceneQuickActionsOptions.ThresholdKey:
                return _options with
                {
                    MatchThresholdPercent = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.MatchThresholdPercent,
                        SceneQuickActionsOptions.ThresholdMinimum,
                        SceneQuickActionsOptions.ThresholdMaximum)
                };
            case SceneQuickActionsOptions.CooldownKey:
                return _options with
                {
                    CooldownMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.CooldownMilliseconds,
                        SceneQuickActionsOptions.CooldownMinimum,
                        SceneQuickActionsOptions.CooldownMaximum)
                };
            case SceneQuickActionsOptions.DropBagHotkeyKey when value is string bagHotkey:
                return TryParseHotkey(bagHotkey, out var parsedBag)
                    ? _options with { DropBagHotkey = parsedBag }
                    : _options;
            case SceneQuickActionsOptions.DropHotbarHotkeyKey when value is string hotbarHotkey:
                return TryParseHotkey(hotbarHotkey, out var parsedHotbar)
                    ? _options with { DropHotbarHotkey = parsedHotbar }
                    : _options;
            case SceneQuickActionsOptions.DropSilenceKey:
                return _options with
                {
                    DropSilenceSeconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.DropSilenceSeconds,
                        SceneQuickActionsOptions.DropSilenceMinimum,
                        SceneQuickActionsOptions.DropSilenceMaximum)
                };
            case SceneQuickActionsOptions.DropSpeedKey when value is string dropSpeed:
                return _options with { DropSpeedIndex = SceneQuickActionsOptions.ParseDropSpeed(dropSpeed) };
            default:
                return _options;
        }
    }

    private static bool TryParseBinding(string text, out PluginInputBinding binding) =>
        PluginInputBinding.TryParse(text, PluginInputBindingKinds.All, out binding);

    /// <summary>热键只接受「单一按键」：组合键无法在钩子里可靠地当触发器，直接拒绝。</summary>
    private static bool TryParseHotkey(string text, out PluginInputBinding hotkey)
    {
        hotkey = new PluginInputBinding();
        return PluginInputBinding.TryParse(text, PluginInputBindingKinds.All, out var parsed)
            && SceneQuickActionsOptions.TryAsHotkey(parsed, out hotkey);
    }
}
