using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoGatling;

[Plugin(
    "auto-gatling",
    DisplayName = "自动加特林",
    Description = "按所选双枪、四枪或六枪方案执行加特林开火和装弹操作。",
    Version = "1.2.0")]
public sealed class AutoGatlingPlugin : PluginBase, IPluginSettingsProvider
{
    private const string EquipmentPlanKey = "equipment-plan";
    private const string ActivationCycleCountKey = "activation-cycle-count";
    private const string InventoryBindingKey = "inventory-binding";
    private const string ActivateBindingKey = "activate-binding";
    private const string ReloadBindingKey = "reload-binding";
    private const string StandardDelayKey = "standard-delay-ms";
    private const string ReloadDelayKey = "reload-delay-ms";
    private const string KeyPressDelayKey = "key-press-delay-ms";
    private const string DragDelayKey = "drag-delay-ms";
    private const string MinimumRandomDelayKey = "minimum-random-delay-ms";
    private const string MaximumRandomDelayKey = "maximum-random-delay-ms";
    private static readonly string[] EquipmentPlanOptions =
        ["双枪", "四枪", "六枪"];
    private static readonly string[] ActivationCycleOptions =
        ["1 轮", "2 轮"];

    private readonly AutoGatlingOptions _options = new();
    private readonly AutoGatlingService _service;
    private PluginInputBinding _inventoryBinding = PluginInputBinding.Keyboard(
        AutoGatlingPlan.DefaultInventoryVirtualKey);
    private PluginInputBinding _activateBinding = PluginInputBinding.Keyboard(
        AutoGatlingPlan.DefaultActivateVirtualKey);
    private PluginInputBinding _reloadBinding = PluginInputBinding.Keyboard(
        AutoGatlingPlan.DefaultReloadVirtualKey);
    // Plugin lifecycle state is intentionally separate from the hook state.
    // A temporarily incomplete/invalid binding can stop the hook while the
    // plugin remains enabled; the next setting change must still retry it.
    private bool _enabled;

    public AutoGatlingPlugin()
    {
        _service = new AutoGatlingService(
            _options,
            message => Context.Logger.Info(message));
    }

    public override string Id => "auto-gatling";

    public override string DisplayName => "自动加特林";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginChoiceSetting
        {
            Key = EquipmentPlanKey,
            DisplayName = "装备方案",
            Description = "选择依次使用背包格 1–2、1–4 或 1–6；默认为双枪。",
            Options = EquipmentPlanOptions,
            DefaultIndex = 0
        },
        new PluginChoiceSetting
        {
            Key = ActivationCycleCountKey,
            DisplayName = "激活循环次数",
            Description = "每次触发“激活加特林一次”时完整执行装备方案的轮数；默认为 1 轮。",
            Options = ActivationCycleOptions,
            DefaultIndex = 0
        },
        new PluginKeyBindingSetting
        {
            Key = InventoryBindingKey,
            DisplayName = "背包按键",
            Description = "打开或关闭背包的游戏内按键，默认为 Tab。",
            DefaultValue = "keyboard:9:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        new PluginKeyBindingSetting
        {
            Key = ActivateBindingKey,
            DisplayName = "激活加特林一次",
            Description = "按所选装备方案和循环次数执行开火；支持组合快捷键，默认为 T。",
            DefaultValue = "keyboard:54:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        new PluginKeyBindingSetting
        {
            Key = ReloadBindingKey,
            DisplayName = "重新装弹",
            Description = "依次为所选装备方案中的全部加特林装弹；支持组合快捷键，默认为 Y。",
            DefaultValue = "keyboard:59:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        CreateDelaySetting(StandardDelayKey, "通用延迟（毫秒）",
            "背包切换、定位、拖放和开火步骤之间的基础等待。", 50),
        CreateDelaySetting(ReloadDelayKey, "装弹延迟（毫秒）",
            "发送装弹按键后等待装弹完成的时间。", 2000),
        CreateDelaySetting(KeyPressDelayKey, "按键保持（毫秒）",
            "键盘或鼠标按下到抬起之间的保持时间。", 10),
        CreateDelaySetting(DragDelayKey, "拖动时长（毫秒）",
            "物品从背包格平滑拖动到快捷栏的基础时长。", 50),
        CreateDelaySetting(MinimumRandomDelayKey, "随机延迟下限（毫秒）",
            "每次等待附加的随机延迟下限；默认安全下限为 30 毫秒。", 30,
            AutoGatlingOptions.MinimumRandomDelayMillisecondsAllowed, 0),
        CreateDelaySetting(MaximumRandomDelayKey, "随机延迟上限（毫秒）",
            "每次等待附加的随机延迟上限；默认安全下限为 50 毫秒。", 50,
            AutoGatlingOptions.MinimumRandomDelayUpperBoundMillisecondsAllowed, 0)
    ];

    public override void OnStart()
    {
        _enabled = true;
        try
        {
            _service.ConfigureBindings(
                _inventoryBinding,
                _activateBinding,
                _reloadBinding);
            _service.Start();
            if (!_service.IsStarted)
            {
                Context.Logger.Warning(
                    "自动加特林未启动：请先在插件设置页面绑定全部三个按键。");
                return;
            }

            Context.Logger.Info(
                $"自动加特林已启动：当前为 {FormatEquipmentPlan(_options.EquipmentSlotCount)}、"
                + $"每次激活 {_options.ActivationCycleCount} 轮；"
                + "当前游戏客户区必须为精确 16:9 或 16:10。 ");
        }
        catch (Exception exception)
        {
            Context.Logger.Error($"自动加特林启动失败：{exception.Message}");
        }
    }

    public override void OnDisable()
    {
        _enabled = false;
        _service.Stop();
    }

    public override void OnUnload()
    {
        _enabled = false;
        _service.Dispose();
    }

    public object? GetSettingValue(string key) => key switch
    {
        EquipmentPlanKey => FormatEquipmentPlan(_options.EquipmentSlotCount),
        ActivationCycleCountKey => FormatActivationCycleCount(
            _options.ActivationCycleCount),
        InventoryBindingKey => _inventoryBinding.StorageValue,
        ActivateBindingKey => _activateBinding.StorageValue,
        ReloadBindingKey => _reloadBinding.StorageValue,
        StandardDelayKey => (double)_options.StandardDelayMilliseconds,
        ReloadDelayKey => (double)_options.ReloadDelayMilliseconds,
        KeyPressDelayKey => (double)_options.KeyPressDelayMilliseconds,
        DragDelayKey => (double)_options.DragDelayMilliseconds,
        MinimumRandomDelayKey => (double)_options.MinimumRandomDelayMilliseconds,
        MaximumRandomDelayKey => (double)_options.MaximumRandomDelayMilliseconds,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (TrySetChoice(key, value))
            return;

        if (TrySetDelay(key, value))
            return;

        if (value is not string text
            || !PluginInputBinding.TryParse(
                text,
                PluginInputBindingKinds.Keyboard,
                out var binding))
        {
            return;
        }

        switch (key)
        {
            case InventoryBindingKey:
                _inventoryBinding = binding;
                break;
            case ActivateBindingKey:
                _activateBinding = binding;
                break;
            case ReloadBindingKey:
                _reloadBinding = binding;
                break;
            default:
                return;
        }

        if (_enabled)
        {
            try
            {
                _service.ConfigureBindings(
                    _inventoryBinding,
                    _activateBinding,
                    _reloadBinding);
                _service.Start();
            }
            catch (Exception exception)
            {
                Context.Logger.Error($"自动加特林绑定更新失败：{exception.Message}");
            }
        }
    }

    private bool TrySetChoice(string key, object? value)
    {
        if (value is not string text)
            return false;

        if (key == EquipmentPlanKey)
        {
            var selectedIndex = Array.IndexOf(EquipmentPlanOptions, text);
            if (selectedIndex >= 0)
                _options.EquipmentSlotCount = (selectedIndex + 1) * 2;
            return true;
        }

        if (key == ActivationCycleCountKey)
        {
            var selectedIndex = Array.IndexOf(ActivationCycleOptions, text);
            if (selectedIndex >= 0)
                _options.ActivationCycleCount = selectedIndex + 1;
            return true;
        }

        return false;
    }

    private bool TrySetDelay(string key, object? value)
    {
        if (!TryConvertToMilliseconds(value, out var milliseconds))
            return false;
        milliseconds = _options.CoerceDelay(milliseconds);
        switch (key)
        {
            case StandardDelayKey:
                _options.StandardDelayMilliseconds = milliseconds;
                return true;
            case ReloadDelayKey:
                _options.ReloadDelayMilliseconds = milliseconds;
                return true;
            case KeyPressDelayKey:
                _options.KeyPressDelayMilliseconds = milliseconds;
                return true;
            case DragDelayKey:
                _options.DragDelayMilliseconds = milliseconds;
                return true;
            case MinimumRandomDelayKey:
                _options.MinimumRandomDelayMilliseconds =
                    _options.CoerceRandomDelay(milliseconds);
                return true;
            case MaximumRandomDelayKey:
                _options.MaximumRandomDelayMilliseconds =
                    _options.CoerceRandomDelayUpperBound(milliseconds);
                return true;
            default:
                return false;
        }
    }

    private static PluginSliderSetting CreateDelaySetting(
        string key,
        string displayName,
        string description,
        double defaultValue,
        double minimum = 0,
        double? minimumWhenUnsafe = null) => new()
    {
        Key = key,
        DisplayName = displayName,
        Description = description,
        Minimum = minimum,
        MinimumWhenUnsafe = minimumWhenUnsafe,
        Maximum = AutoGatlingOptions.MaximumDelayMilliseconds,
        StepFrequency = 1,
        DefaultValue = defaultValue
    };

    private static string FormatEquipmentPlan(int slotCount) => slotCount switch
    {
        4 => EquipmentPlanOptions[1],
        6 => EquipmentPlanOptions[2],
        _ => EquipmentPlanOptions[0]
    };

    private static string FormatActivationCycleCount(int cycleCount) =>
        ActivationCycleOptions[
            Math.Clamp(
                cycleCount,
                1,
                AutoGatlingOptions.MaximumActivationCycleCount) - 1];

    private static bool TryConvertToMilliseconds(object? value, out int milliseconds)
    {
        switch (value)
        {
            case double number when double.IsFinite(number):
                milliseconds = (int)Math.Round(number);
                return true;
            case long number:
                milliseconds = (int)Math.Clamp(number, int.MinValue, int.MaxValue);
                return true;
            case int number:
                milliseconds = number;
                return true;
            default:
                milliseconds = 0;
                return false;
        }
    }
}
