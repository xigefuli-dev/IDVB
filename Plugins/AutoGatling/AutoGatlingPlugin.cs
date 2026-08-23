using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoGatling;

[Plugin(
    "auto-gatling",
    DisplayName = "自动加特林",
    Description = "依次使用六个背包格执行加特林开火和装弹操作。",
    Version = "1.0.0")]
public sealed class AutoGatlingPlugin : PluginBase, IPluginSettingsProvider
{
    private const string InventoryBindingKey = "inventory-binding";
    private const string ActivateBindingKey = "activate-binding";
    private const string ReloadBindingKey = "reload-binding";
    private const string StandardDelayKey = "standard-delay-ms";
    private const string ReloadDelayKey = "reload-delay-ms";
    private const string KeyPressDelayKey = "key-press-delay-ms";
    private const string DragDelayKey = "drag-delay-ms";
    private const string MinimumRandomDelayKey = "minimum-random-delay-ms";
    private const string MaximumRandomDelayKey = "maximum-random-delay-ms";

    private readonly AutoGatlingOptions _options = new();
    private readonly AutoGatlingService _service;
    private PluginInputBinding _inventoryBinding = PluginInputBinding.Keyboard(
        AutoGatlingPlan.DefaultInventoryVirtualKey);
    private PluginInputBinding _activateBinding = PluginInputBinding.Keyboard(
        AutoGatlingPlan.DefaultActivateVirtualKey);
    private PluginInputBinding _reloadBinding = PluginInputBinding.Keyboard(
        AutoGatlingPlan.DefaultReloadVirtualKey);
    private bool _started;

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
            Description = "依次使用六个背包格执行一次开火循环；支持组合快捷键，默认为 T。",
            DefaultValue = "keyboard:54:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        },
        new PluginKeyBindingSetting
        {
            Key = ReloadBindingKey,
            DisplayName = "重新装弹",
            Description = "依次使用六个背包格执行装弹循环；支持组合快捷键，默认为 Y。",
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
            "每次等待额外加入的随机延迟下限。", 10),
        CreateDelaySetting(MaximumRandomDelayKey, "随机延迟上限（毫秒）",
            "每次等待额外加入的随机延迟上限；低于下限时自动按有序范围使用。", 20)
    ];

    public override void OnStart()
    {
        try
        {
            _service.ConfigureBindings(
                _inventoryBinding,
                _activateBinding,
                _reloadBinding);
            _service.Start();
            _started = _service.IsStarted;
            if (!_started)
            {
                Context.Logger.Warning(
                    "自动加特林未启动：请先在插件设置页面绑定全部三个按键。");
                return;
            }

            Context.Logger.Info(
                "自动加特林已启动：T 执行六格开火，Y 执行六格装弹；"
                + "当前游戏客户区必须为精确 16:9 或 16:10。 ");
        }
        catch (Exception exception)
        {
            _started = false;
            Context.Logger.Error($"自动加特林启动失败：{exception.Message}");
        }
    }

    public override void OnDisable()
    {
        _started = false;
        _service.Stop();
    }

    public override void OnUnload()
    {
        _started = false;
        _service.Dispose();
    }

    public object? GetSettingValue(string key) => key switch
    {
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

        if (_started)
        {
            try
            {
                _service.ConfigureBindings(
                    _inventoryBinding,
                    _activateBinding,
                    _reloadBinding);
                _service.Start();
                _started = _service.IsStarted;
            }
            catch (Exception exception)
            {
                _started = false;
                Context.Logger.Error($"自动加特林绑定更新失败：{exception.Message}");
            }
        }
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
                _options.MinimumRandomDelayMilliseconds = milliseconds;
                return true;
            case MaximumRandomDelayKey:
                _options.MaximumRandomDelayMilliseconds = milliseconds;
                return true;
            default:
                return false;
        }
    }

    private static PluginSliderSetting CreateDelaySetting(
        string key,
        string displayName,
        string description,
        double defaultValue) => new()
    {
        Key = key,
        DisplayName = displayName,
        Description = description,
        Minimum = 0,
        Maximum = AutoGatlingOptions.MaximumDelayMilliseconds,
        StepFrequency = 1,
        DefaultValue = defaultValue
    };

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
