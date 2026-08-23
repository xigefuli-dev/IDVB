using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.NightVision;

[Plugin(
    "night-vision",
    DisplayName = "全屏亮度增益",
    Description = "按自定义切换键切换全屏线性亮度增益；所有 RGB 通道按相同倍率调整，高光可能被裁剪。",
    Version = "1.0.0")]
public sealed class NightVisionPlugin : PluginBase, IPluginSettingsProvider
{
    public const double MaximumBrightnessPercent = 2000;
    private const string BindingKey = "toggle-binding";
    private const string BrightnessKey = "brightness-percent";

    private readonly NightVisionFilter _filter = new();
    private double _brightnessPercent = 100;
    private IPluginInputService? _input;
    private PluginInputBinding _binding = PluginInputBinding.Keyboard(0x12);
    private volatile bool _enabled;

    public override string Id => "night-vision";
    public override string DisplayName => "全屏亮度增益";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginSliderSetting
        {
            Key = BrightnessKey,
            DisplayName = "全屏增益幅度（%）",
            Description = "对整个桌面应用相同的 RGB 线性增益；0% 为原始画面，100% 为 2 倍亮度，最大允许 2000%，高值可能裁剪高光。",
            Minimum = 0,
            Maximum = MaximumBrightnessPercent,
            StepFrequency = 1,
            DefaultValue = 100
        },
        new PluginKeyBindingSetting
        {
            Key = BindingKey,
            DisplayName = "亮度切换键",
            Description = "按下此键切换全屏亮度增益；支持键盘组合键或鼠标键。",
            DefaultValue = "keyboard:12:0"
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _input = context.GetService<IPluginInputService>();
        if (_input is null)
        {
            context.Logger.Error("无法取得插件输入服务，亮度切换不可用。");
            return;
        }
        _input.BindingInvoked += OnBindingInvoked;
    }

    public override void OnEnable()
    {
        _enabled = true;
        ApplyBinding();
        _filter.SetBrightnessPercent(_brightnessPercent);
    }

    public override void OnDisable()
    {
        _enabled = false;
        _input?.ClearBindings(Context.PluginId);
        _filter.Disable();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_input is not null)
        {
            _input.BindingInvoked -= OnBindingInvoked;
            _input.ClearBindings(Context.PluginId);
        }
        _filter.Dispose();
        _input = null;
    }

    public object? GetSettingValue(string key) => key switch
    {
        BrightnessKey => _brightnessPercent,
        BindingKey => _binding.StorageValue,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (key == BrightnessKey && TryReadDouble(value, out var brightness))
        {
            _brightnessPercent = Math.Clamp(brightness, 0, MaximumBrightnessPercent);
            _filter.SetBrightnessPercent(_brightnessPercent);
            return;
        }

        if (key == BindingKey
            && value is string text
            && PluginInputBinding.TryParse(text, out var binding))
        {
            _binding = binding;
            if (_enabled)
                ApplyBinding();
        }
    }

    private void ApplyBinding() =>
        _input?.SetBinding(Context.PluginId, BindingKey, _binding);

    private void OnBindingInvoked(object? sender, PluginInputEventArgs args)
    {
        if (!_enabled
            || !args.IsDown
            || !string.Equals(args.PluginId, Context.PluginId, StringComparison.Ordinal)
            || !string.Equals(args.BindingKey, BindingKey, StringComparison.Ordinal))
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
