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
    DisplayName = "连点器",
    Description = "按住鼠标右键超过 0.1 秒后，以可调周期发送完整 F↓/F↑ 事件，松开即停。"
        + "可在插件设置中分别调整按下后与抬手后的延迟。",
    Version = "1.3.0")]
public sealed class AutoClickerPlugin : PluginBase, IPluginSettingsProvider
{
    private const string KeyDownDelayKey = "key-down-delay-ms";
    private const string UpToNextDownDelayKey = "up-to-next-down-delay-ms";

    private readonly AutoClickerOptions _options = new();
    private readonly AutoClickerService _service;

    public AutoClickerPlugin()
    {
        _service = new AutoClickerService(_options);
    }

    public override string Id => "auto-clicker";

    public override string DisplayName => "连点器";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginSliderSetting
        {
            Key = KeyDownDelayKey,
            DisplayName = "按下后延迟（毫秒）",
            Description = "每次连点中 F 键按下后保持的时间，再抬起。",
            Minimum = 1,
            Maximum = AutoClickerOptions.MaxKeyDownDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = AutoClickerOptions.DefaultKeyDownDelayMilliseconds
        },
        new PluginSliderSetting
        {
            Key = UpToNextDownDelayKey,
            DisplayName = "抬手后延迟（毫秒）",
            Description = "每次连点中 F 键抬起后到下一次按下的间隔。",
            Minimum = 1,
            Maximum = AutoClickerOptions.MaxUpToNextDownDelayMilliseconds,
            StepFrequency = 1,
            DefaultValue = AutoClickerOptions.DefaultUpToNextDownDelayMilliseconds
        }
    ];

    public override void OnStart()
    {
        try
        {
            _service.Start();
            Context.Logger.Info(
                $"连点器已启动：按住鼠标右键超过 {AutoClickerPolicy.HoldBeforeClickMilliseconds}ms 后，"
                + $"以 {_options.TotalPeriodMilliseconds}ms 周期发送完整 F↓/F↑ 事件，松开停止。"
                + " 可在插件设置中调整按下/抬手延迟。");
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
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (!TryConvertToMilliseconds(value, out var milliseconds))
            return;
        switch (key)
        {
            case KeyDownDelayKey:
                _options.KeyDownDelayMilliseconds = milliseconds;
                break;
            case UpToNextDownDelayKey:
                _options.UpToNextDownDelayMilliseconds = milliseconds;
                break;
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
