using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.PickupEscape;

/// <summary>
/// 关闭拾取面板（内部 Id 仍是 <c>pickup-escape</c>）：把「可拾取」面板的关闭动作自动化。
///
/// 探索模式里走到箱子前，右上角会弹出「可拾取」面板（标题栏右侧写着 ESC，右上角一个 ✕）。
/// 本插件用「背包辅助」那两张现成模板（「拾取全部」文案 / 「快捷替换」按钮）判断面板在不在，
/// 面板刚出现时按一下 ESC 把它关掉 —— 与手点那个 ✕ 等价。
///
/// 除此之外还管三件事：
///   ① 只有面板**连续出现够久**（默认 250ms）才按 ESC —— 一闪而过的不算（第一道防线）；
///   ② 万一还是把游戏设置界面开出来了（那一下 ESC 没东西可关），
///      认出设置界面后再补一下 ESC 退回上一界面（第二道防线）；
///   ③ 按下游戏里「展开拾取列表」的键（默认 H）之后若干秒内完全不动作，
///      免得把你刚手动展开的列表立刻关掉。
///
/// 与「背包辅助」的区别只有动作：背包辅助是「拾取全部（默认 B）+ 快捷替换」，
/// 本插件是「直接关掉面板、什么都不捡」。两者会盯同一块面板，建议只开一个。
///
/// 识别方式是对宿主抓到的客户区画面做多尺度模板匹配，模板来自 1920×1080 游戏截图；
/// 命中后才注入输入，注入前会再次确认游戏仍在前台。不读内存、不注入进程、不改游戏文件。
///
/// 常驻（<c>AlwaysActive</c>）：热键与「展开拾取列表」键要在局外也按得动，与「背包辅助」一致。
/// </summary>
[Plugin(
    PickupEscapeOptions.PluginId,
    DisplayName = "关闭拾取面板",
    Description = "「可拾取」面板连续出现够久时自动按一下 ESC 关掉它（一闪而过的不按）；误开设置界面时补按 ESC 退回；按「展开拾取列表」（默认 H）后短暂让路。",
    Version = "1.3.0",
    AlwaysActive = true)]
public sealed class PickupEscapePlugin
    : PluginBase, IPluginSettingsProvider, IHandle<SessionStateChangedMessage>
{
    private PickupEscapeMatcher? _matcher;
    private PickupEscapeRunner? _runner;
    private PickupEscapeOptions _options = new();
    private IGameOverlayToast? _toast;
    private bool _enabled;

    public override string Id => PickupEscapeOptions.PluginId;

    public override string DisplayName => "关闭拾取面板";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginKeyBindingSetting
        {
            Key = PickupEscapeOptions.ToggleHotkeyKey,
            DisplayName = "开关检测 · 热键（键盘键或鼠标键）",
            Description = "按一下暂停检测、再按一下恢复；默认「未设置」，点右边按钮录一个键即可（不支持组合键）。",
            DefaultValue = PickupEscapeOptions.NoBindingStorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = PickupEscapeOptions.PickupListBindingKey,
            DisplayName = "展开拾取列表 · 按键",
            Description = "游戏里展开拾取列表的那个键（默认 H）。按下它之后的若干秒内，本插件不按 ESC。",
            DefaultValue = PluginInputBinding.Keyboard(PickupEscapeOptions.DefaultPickupListVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = PickupEscapeOptions.ActionBindingKey,
            DisplayName = "识别到面板时按下的键",
            Description = "游戏里「可拾取」面板标题右边的提示键（默认 Esc，等价于点右上角的 ✕）。",
            Group = PickupEscapeOptions.ActionGroupTitle,
            DefaultValue = PluginInputBinding.Keyboard(PickupEscapeOptions.DefaultActionVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.PauseAfterPickupListKey,
            DisplayName = "按「展开拾取列表」后暂停（秒）",
            Description = "按下上面那个键之后，这么多秒内不按 ESC —— 免得把你刚展开的列表立刻关掉。",
            Group = PickupEscapeOptions.ActionGroupTitle,
            Minimum = PickupEscapeOptions.PauseSecondsMinimum,
            Maximum = PickupEscapeOptions.PauseSecondsMaximum,
            StepFrequency = 1,
            DefaultValue = 10
        },
        new PluginToggleSetting
        {
            Key = PickupEscapeOptions.EscapeSettingsKey,
            DisplayName = "误开设置界面时补按 ESC 退回",
            Description = "那一下 ESC 没东西可关时会开到游戏设置界面；打开本项后，认出设置界面会再补一下 ESC 退回去（第二道防线）。",
            Group = PickupEscapeOptions.ActionGroupTitle,
            DefaultValue = true
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.MinimumVisibleKey,
            DisplayName = "面板需持续出现（毫秒）",
            Description = "「可拾取」面板要连续出现这么久才按 ESC；一闪而过的不算（那种情况 ESC 没东西可关，反而会开出游戏设置界面）。0 = 一认出就按。",
            Group = PickupEscapeOptions.TimingGroupTitle,
            Minimum = PickupEscapeOptions.MinimumVisibleMinimum,
            Maximum = PickupEscapeOptions.MinimumVisibleMaximum,
            StepFrequency = 10,
            DefaultValue = 250
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.PollIntervalKey,
            DisplayName = "检测间隔（毫秒）",
            Description = "多久抓一帧做识别；只在游戏处于前台时才会真正抓帧。",
            Group = PickupEscapeOptions.TimingGroupTitle,
            Minimum = PickupEscapeOptions.PollIntervalMinimum,
            Maximum = PickupEscapeOptions.PollIntervalMaximum,
            StepFrequency = 50,
            DefaultValue = 400
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.ThresholdKey,
            DisplayName = "识别相似度阈值（%）",
            Description = "越高越不容易误判；界面被遮挡或分辨率差异大、一直认不出来时调低一些。",
            Group = PickupEscapeOptions.TimingGroupTitle,
            Minimum = PickupEscapeOptions.ThresholdMinimum,
            Maximum = PickupEscapeOptions.ThresholdMaximum,
            StepFrequency = 1,
            DefaultValue = 80
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.CooldownKey,
            DisplayName = "同一面板动作冷却（毫秒）",
            Description = "面板消失后重新出现时，两次按键之间的最小间隔，避免连着按。",
            Group = PickupEscapeOptions.TimingGroupTitle,
            Minimum = PickupEscapeOptions.CooldownMinimum,
            Maximum = PickupEscapeOptions.CooldownMaximum,
            StepFrequency = 100,
            DefaultValue = 1500
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.MinimumRandomDelayKey,
            DisplayName = "随机延迟下限（毫秒）",
            Description = "每次按键后追加的随机等待下限。",
            Group = PickupEscapeOptions.TimingGroupTitle,
            Minimum = 30,
            MinimumWhenUnsafe = 0,
            Maximum = PickupEscapeOptions.MaximumRandomDelayMillisecondsAllowed,
            StepFrequency = 1,
            DefaultValue = 30
        },
        new PluginSliderSetting
        {
            Key = PickupEscapeOptions.MaximumRandomDelayKey,
            DisplayName = "随机延迟上限（毫秒）",
            Description = "每次按键后从下限至上限中独立随机取值，追加在原有等待之后。",
            Group = PickupEscapeOptions.TimingGroupTitle,
            Minimum = 50,
            MinimumWhenUnsafe = 0,
            Maximum = PickupEscapeOptions.MaximumRandomDelayMillisecondsAllowed,
            StepFrequency = 1,
            DefaultValue = 50
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);

        try
        {
            _matcher = new PickupEscapeMatcher();
        }
        catch (Exception exception)
        {
            context.Logger.Error($"关闭拾取面板加载识别模板失败，插件不可用：{exception.Message}");
            return;
        }

        var screenshot = context.GetService<IPluginScreenshotService>();
        var gameWindow = context.GetService<IPluginGameWindowService>();
        if (screenshot is null && gameWindow is null)
        {
            context.Logger.Error("宿主没有提供截图能力，关闭拾取面板不可用。");
            return;
        }

        _toast = context.GetService<IGameOverlayToast>();
        if (_toast is null)
            context.Logger.Warning("宿主没有提供浮层提示通道，切换检测开关时屏幕上不会有提示（功能不受影响）。");

        var grabber = new PickupEscapeFrameGrabber(
            context,
            screenshot,
            gameWindow,
            message => context.Logger.Info(message));
        _runner = new PickupEscapeRunner(grabber, _matcher, context.Logger);
        _runner.DetectionStateChanged += OnDetectionStateChanged;
        _runner.AttachHotkeys(context.GetService<IPluginInputService>(), Id);
        _runner.SetOptions(_options);
    }

    public override void OnEnable()
    {
        _enabled = true;
        if (_runner is null)
        {
            Context.Logger.Error("关闭拾取面板未成功初始化，无法启动检测。");
            return;
        }

        _runner.SetOptions(_options);
        _runner.ResetDetection();
        _runner.Start();
        Context.Logger.Info(
            $"参数：检测间隔 {_options.PollIntervalMilliseconds}ms，阈值 {_options.MatchThresholdPercent}%，"
            + $"识别到「可拾取」面板时按 {_options.ActionBinding.DisplayName}，"
            + $"动作冷却 {_options.CooldownMilliseconds}ms，"
            + $"开关热键 {(_options.ToggleHotkey.IsConfigured ? _options.ToggleHotkey.DisplayName : "未设置")}；"
            + $"「{_options.PickupListBinding.DisplayName}」后暂停 {_options.PauseAfterPickupListSeconds} 秒，"
            + $"面板需连续出现 {_options.MinimumVisibleMilliseconds}ms 才按，"
            + $"误开设置界面补按 ESC {(_options.EscapeSettingsEnabled ? "开" : "关")}。");
    }

    public override void OnDisable()
    {
        _enabled = false;
        _runner?.Stop();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_runner is not null)
            _runner.DetectionStateChanged -= OnDetectionStateChanged;
        _runner?.Dispose();
        _runner = null;
        _matcher?.Dispose();
        _matcher = null;
    }

    /// <summary>热键键位在设置页改动后，重新注册到宿主输入通道。</summary>
    public void RefreshHotkeys() => _runner?.RefreshHotkeyBindings();

    public void Handle(SessionStateChangedMessage message) =>
        _runner?.SetMapOpen(message.GameMapOpen);

    /// <summary>热键换了检测开关：屏幕上来一条游戏内提示，避免「按下去像没反应」。</summary>
    private void OnDetectionStateChanged(bool enabled)
    {
        try
        {
            if (enabled)
                _toast?.Notice("关闭拾取面板：检测已开启");
            else
                _toast?.Warning("关闭拾取面板：检测已暂停");
        }
        catch (Exception exception)
        {
            Context.Logger.Warning($"发布浮层提示失败：{exception.Message}");
        }
    }

    public object? GetSettingValue(string key) => key switch
    {
        PickupEscapeOptions.ToggleHotkeyKey => _options.ToggleHotkey.StorageValue,
        PickupEscapeOptions.PickupListBindingKey => _options.PickupListBinding.StorageValue,
        PickupEscapeOptions.PauseAfterPickupListKey => (double)_options.PauseAfterPickupListSeconds,
        PickupEscapeOptions.MinimumVisibleKey => (double)_options.MinimumVisibleMilliseconds,
        PickupEscapeOptions.EscapeSettingsKey => _options.EscapeSettingsEnabled,
        PickupEscapeOptions.ActionBindingKey => _options.ActionBinding.StorageValue,
        PickupEscapeOptions.PollIntervalKey => (double)_options.PollIntervalMilliseconds,
        PickupEscapeOptions.ThresholdKey => (double)_options.MatchThresholdPercent,
        PickupEscapeOptions.CooldownKey => (double)_options.CooldownMilliseconds,
        PickupEscapeOptions.MinimumRandomDelayKey => (double)_options.MinimumRandomDelayMilliseconds,
        PickupEscapeOptions.MaximumRandomDelayKey => (double)_options.MaximumRandomDelayMilliseconds,
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

    private PickupEscapeOptions BuildOptions(string key, object? value)
    {
        switch (key)
        {
            case PickupEscapeOptions.ToggleHotkeyKey when value is string hotkey:
                // 「none」= 用户点了重置：允许把热键清成「未设置」，之后不会再注册任何绑定。
                if (string.Equals(hotkey, PickupEscapeOptions.NoBindingStorageValue, StringComparison.Ordinal))
                    return _options with { ToggleHotkey = new PluginInputBinding() };
                return TryParseHotkey(hotkey, out var parsedHotkey)
                    ? _options with { ToggleHotkey = parsedHotkey }
                    : _options;
            case PickupEscapeOptions.PickupListBindingKey when value is string pickupList:
                // 「展开拾取列表」也允许清空（清空后就没有"按 H 让路"这回事了）。
                if (string.Equals(pickupList, PickupEscapeOptions.NoBindingStorageValue, StringComparison.Ordinal))
                    return _options with { PickupListBinding = new PluginInputBinding() };
                return TryParseBinding(pickupList, out var parsedPickupList)
                    ? _options with { PickupListBinding = parsedPickupList }
                    : _options;
            case PickupEscapeOptions.PauseAfterPickupListKey:
                return _options with
                {
                    PauseAfterPickupListSeconds = PickupEscapeOptions.FromDouble(
                        value,
                        _options.PauseAfterPickupListSeconds,
                        PickupEscapeOptions.PauseSecondsMinimum,
                        PickupEscapeOptions.PauseSecondsMaximum)
                };
            case PickupEscapeOptions.MinimumVisibleKey:
                return _options with
                {
                    MinimumVisibleMilliseconds = PickupEscapeOptions.FromDouble(
                        value,
                        _options.MinimumVisibleMilliseconds,
                        PickupEscapeOptions.MinimumVisibleMinimum,
                        PickupEscapeOptions.MinimumVisibleMaximum)
                };
            case PickupEscapeOptions.EscapeSettingsKey when value is bool escapeSettings:
                return _options with { EscapeSettingsEnabled = escapeSettings };
            case PickupEscapeOptions.ActionBindingKey when value is string action:
                return TryParseBinding(action, out var parsedAction)
                    ? _options with { ActionBinding = parsedAction }
                    : _options;
            case PickupEscapeOptions.PollIntervalKey:
                return _options with
                {
                    PollIntervalMilliseconds = PickupEscapeOptions.FromDouble(
                        value,
                        _options.PollIntervalMilliseconds,
                        PickupEscapeOptions.PollIntervalMinimum,
                        PickupEscapeOptions.PollIntervalMaximum)
                };
            case PickupEscapeOptions.ThresholdKey:
                return _options with
                {
                    MatchThresholdPercent = PickupEscapeOptions.FromDouble(
                        value,
                        _options.MatchThresholdPercent,
                        PickupEscapeOptions.ThresholdMinimum,
                        PickupEscapeOptions.ThresholdMaximum)
                };
            case PickupEscapeOptions.CooldownKey:
                return _options with
                {
                    CooldownMilliseconds = PickupEscapeOptions.FromDouble(
                        value,
                        _options.CooldownMilliseconds,
                        PickupEscapeOptions.CooldownMinimum,
                        PickupEscapeOptions.CooldownMaximum)
                };
            case PickupEscapeOptions.MinimumRandomDelayKey:
                return _options with
                {
                    MinimumRandomDelayMilliseconds = PickupEscapeOptions.FromDouble(value,
                        _options.MinimumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(30),
                        PickupEscapeOptions.MaximumRandomDelayMillisecondsAllowed)
                };
            case PickupEscapeOptions.MaximumRandomDelayKey:
                return _options with
                {
                    MaximumRandomDelayMilliseconds = PickupEscapeOptions.FromDouble(value,
                        _options.MaximumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(50),
                        PickupEscapeOptions.MaximumRandomDelayMillisecondsAllowed)
                };
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
            && PickupEscapeOptions.TryAsHotkey(parsed, out hotkey);
    }
}
