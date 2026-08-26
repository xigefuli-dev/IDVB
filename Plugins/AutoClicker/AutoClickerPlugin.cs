using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoClicker;

/// <summary>
/// Right-button hold auto-clicker. After the hold threshold, the physical
/// right button is handed off and complete F down/up events are injected.
/// Releasing the right button stops the service and forces an F key-up.
/// 按下后 / 抬手后延迟可在插件设置页（TTM）中调整。
/// </summary>
[Plugin(
    "auto-clicker",
    DisplayName = "开棺无需连按",
    Description = "按住自定义触发键即可自动开棺，无需连续按键；松开触发键后停止。",
    Version = "1.4.0")]
public sealed class AutoClickerPlugin : PluginBase, IPluginSettingsProvider
{
    private const string TriggerBindingKey = "trigger-binding";
    private const string OutputBindingKey = "output-binding";
    private const string KeyDownDelayKey = "key-down-delay-ms";
    private const string UpToNextDownDelayKey = "up-to-next-down-delay-ms";
    private const string RandomDelayLowerBoundKey = "minimum-random-delay-ms";
    private const string RandomDelayUpperBoundKey = "maximum-random-delay-ms";

    private readonly AutoClickerOptions _options = new();
    private readonly AutoClickerService _service;
    private PluginInputBinding _triggerBinding =
        PluginInputBinding.Mouse(PluginMouseButton.Right);
    private PluginInputBinding _outputBinding = PluginInputBinding.Keyboard(0x46);

    public AutoClickerPlugin()
    {
        _service = new AutoClickerService(_options);
    }

    public override string Id => "auto-clicker";

    public override string DisplayName => "开棺无需连按";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginSliderSetting
        {
            Key = KeyDownDelayKey,
            DisplayName = "按下后延迟（毫秒）",
            Description = "每次连点中发送按键按下后保持的时间，再抬起。",
            Minimum = 1,
            Maximum = AutoClickerOptions.MaxKeyDownDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = AutoClickerOptions.DefaultKeyDownDelayMilliseconds
        },
        new PluginSliderSetting
        {
            Key = UpToNextDownDelayKey,
            DisplayName = "抬手后延迟（毫秒）",
            Description = "每次连点中发送按键抬起后到下一次按下的间隔。",
            Minimum = 1,
            Maximum = AutoClickerOptions.MaxUpToNextDownDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = AutoClickerOptions.DefaultUpToNextDownDelayMilliseconds
        },
        new PluginSliderSetting
        {
            Key = RandomDelayLowerBoundKey,
            DisplayName = "随机延迟下限（毫秒）",
            Description = "每段等待附加的随机延迟下限；默认安全下限为 30 毫秒。",
            Minimum = PluginRandomDelayPolicy.GetMinimum(AutoClickerOptions.MinimumRandomDelayMilliseconds),
            MinimumWhenUnsafe = 0,
            Maximum = AutoClickerOptions.MaximumRandomDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = AutoClickerOptions.MinimumRandomDelayMilliseconds
        },
        new PluginSliderSetting
        {
            Key = RandomDelayUpperBoundKey,
            DisplayName = "随机延迟上限（毫秒）",
            Description = "每段等待附加的随机延迟上限；默认安全下限为 50 毫秒。",
            Minimum = PluginRandomDelayPolicy.GetMinimum(AutoClickerOptions.MinimumRandomDelayUpperBoundMilliseconds),
            MinimumWhenUnsafe = 0,
            Maximum = AutoClickerOptions.MaximumRandomDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = AutoClickerOptions.DefaultMaximumRandomDelayMilliseconds
        },
        new PluginKeyBindingSetting
        {
            Key = TriggerBindingKey,
            DisplayName = "触发按键",
            Description = "按住此键达到长按阈值后启动连点；支持键盘组合键或鼠标键。",
            DefaultValue = "mouse:1"
        },
        new PluginKeyBindingSetting
        {
            Key = OutputBindingKey,
            DisplayName = "发送按键",
            Description = "连点器循环发送的键盘按键。",
            DefaultValue = "keyboard:46:0",
            AllowedKinds = PluginInputBindingKinds.Keyboard
        }
    ];

    public override void OnStart()
    {
        try
        {
            _service.ConfigureBindings(_triggerBinding, _outputBinding);
            _service.Start();
            Context.Logger.Info(
                $"连点器已启动：按住自定义触发键超过 {AutoClickerPolicy.HoldBeforeClickMilliseconds}ms 后，"
                + $"以 {_options.TotalPeriodMilliseconds}ms 基础周期发送完整按下/抬起事件，"
                + "每段强制加入不低于 30ms 的随机延迟，松开停止。"
                + " 可在插件设置中调整按键、基础延迟与随机延迟范围。");
        }
        catch (Exception exception)
        {
            // Hook installation failure only disables this plugin; it must not
            // prevent the host application from starting.
            Context.Logger.Error($"连点器启动失败：{exception.Message}");
        }
    }

    public override void OnDisable() => _service.Stop();

    public object? GetSettingValue(string key) => key switch
    {
        KeyDownDelayKey => (double)_options.KeyDownDelayMilliseconds,
        UpToNextDownDelayKey => (double)_options.UpToNextDownDelayMilliseconds,
        RandomDelayLowerBoundKey => (double)_options.RandomDelayLowerBoundMilliseconds,
        RandomDelayUpperBoundKey => (double)_options.RandomDelayUpperBoundMilliseconds,
        TriggerBindingKey => _triggerBinding.StorageValue,
        OutputBindingKey => _outputBinding.StorageValue,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (key is KeyDownDelayKey or UpToNextDownDelayKey
                or RandomDelayLowerBoundKey or RandomDelayUpperBoundKey
            && TryConvertToMilliseconds(value, out var milliseconds))
        {
            if (key == KeyDownDelayKey)
                _options.KeyDownDelayMilliseconds = milliseconds;
            else if (key == UpToNextDownDelayKey)
                _options.UpToNextDownDelayMilliseconds = milliseconds;
            else if (key == RandomDelayLowerBoundKey)
                _options.RandomDelayLowerBoundMilliseconds = milliseconds;
            else
                _options.RandomDelayUpperBoundMilliseconds = milliseconds;
            return;
        }

        if (key == TriggerBindingKey
            && value is string triggerText
            && PluginInputBinding.TryParse(triggerText, out var trigger))
        {
            _triggerBinding = trigger;
            if (Context is not null)
            {
                _service.ConfigureBindings(_triggerBinding, _outputBinding);
                _service.Start();
            }
            return;
        }

        if (key == OutputBindingKey
            && value is string outputText
            && PluginInputBinding.TryParse(
                outputText,
                PluginInputBindingKinds.Keyboard,
                out var output))
        {
            _outputBinding = output;
            if (Context is not null)
            {
                _service.ConfigureBindings(_triggerBinding, _outputBinding);
                _service.Start();
            }
        }
    }

    private static bool TryConvertToMilliseconds(object? value, out int milliseconds)
    {
        switch (value)
        {
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                milliseconds = (int)Math.Round(d);
                return true;
            case long l:
                milliseconds = (int)Math.Clamp(l, int.MinValue, int.MaxValue);
                return true;
            case int i:
                milliseconds = i;
                return true;
            default:
                milliseconds = 0;
                return false;
        }
    }
}
