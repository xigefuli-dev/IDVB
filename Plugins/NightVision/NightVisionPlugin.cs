using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.NightVision;

[Plugin(
    "night-vision",
    DisplayName = "夜视滤镜",
    Description = "按 Alt 切换全屏夜视滤镜，只增强低亮度区域。",
    Version = "1.0.0")]
public sealed class NightVisionPlugin : PluginBase, IPluginSettingsProvider
{
    public const double MaximumBrightnessPercent = 2000;
    private const string BrightnessKey = "brightness-percent";

    private readonly NightVisionFilter _filter = new();
    private double _brightnessPercent = 100;
    private IGlobalInput? _input;
    private volatile bool _enabled;

    public override string Id => "night-vision";
    public override string DisplayName => "夜视滤镜";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginSliderSetting
        {
            Key = BrightnessKey,
            DisplayName = "低亮度增亮（%）",
            Description = "仅对接近黑暗的画面进行提亮，最大允许 2000%。",
            Minimum = 0,
            Maximum = MaximumBrightnessPercent,
            StepFrequency = 1,
            DefaultValue = 100
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _input = context.GetService<IGlobalInput>();
        if (_input is null)
        {
            context.Logger.Error("无法取得全局输入服务，Alt 切换不可用。");
            return;
        }
    }

    public override void OnEnable()
    {
        _enabled = true;
        _input?.AltInvoked += OnAltInvoked;
        _filter.SetBrightnessPercent(_brightnessPercent);
    }

    public override void OnDisable()
    {
        _enabled = false;
        if (_input is not null)
            _input.AltInvoked -= OnAltInvoked;
        _filter.Disable();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_input is not null)
            _input.AltInvoked -= OnAltInvoked;
        _filter.Dispose();
        _input = null;
    }

    public object? GetSettingValue(string key) =>
        key == BrightnessKey ? _brightnessPercent : null;

    public void SetSettingValue(string key, object? value)
    {
        if (key != BrightnessKey || !TryReadDouble(value, out var brightness))
            return;
        _brightnessPercent = Math.Clamp(brightness, 0, MaximumBrightnessPercent);
        _filter.SetBrightnessPercent(_brightnessPercent);
    }

    private void OnAltInvoked(object? sender, object args)
    {
        if (!_enabled)
            return;

        void ToggleOnUiThread()
        {
            if (_enabled)
                _filter.Toggle();
        }

        if (Context.UI.HasThreadAccess)
        {
            ToggleOnUiThread();
            return;
        }

        Context.UI.TryPost(ToggleOnUiThread);
    }

    private static bool TryReadDouble(object? value, out double result)
    {
        result = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => double.NaN
        };
        return !double.IsNaN(result) && !double.IsInfinity(result);
    }
}
