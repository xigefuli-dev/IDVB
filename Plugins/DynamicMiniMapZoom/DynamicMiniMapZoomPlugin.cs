using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.DynamicMiniMapZoom;

[Plugin(
    "dynamic-minimap-zoom",
    DisplayName = "动态小地图缩放",
    Description = "进入对局后按住自定义辅助键滚动鼠标滚轮，临时调整小地图大小；结束对局后自动恢复。",
    Version = "1.0.0")]
public sealed class DynamicMiniMapZoomPlugin : PluginBase, IHandle<MatchStateChangedMessage>, IPluginSettingsProvider
{
    private const string BindingKey = "wheel-modifier-binding";
    private const string SensitivityKey = "wheel-sensitivity-percent";
    private IGlobalInput? _input;
    private IPluginInputService? _pluginInput;
    private IOverlayWindow? _overlay;
    private ISessionOrchestrator? _session;
    private bool _enabled;
    private bool _matchStarted;
    private string? _matchId;
    private PluginInputBinding _binding = PluginInputBinding.Keyboard(0x14);
    private double _sensitivityPercent = DynamicMiniMapZoomPolicy.DefaultSensitivityPercent;

    public override string Id => "dynamic-minimap-zoom";
    public override string DisplayName => "动态小地图缩放";

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _input = context.GetService<IGlobalInput>();
        _pluginInput = context.GetService<IPluginInputService>();
        _overlay = context.GetService<IOverlayWindow>();
        _session = context.GetService<ISessionOrchestrator>();

        if (_input is null)
            context.Logger.Error("无法取得全局输入服务，动态小地图缩放不可用。");
        if (_overlay is null)
            context.Logger.Error("无法取得叠加窗口服务，动态小地图缩放不可用。");
        if (_pluginInput is null)
            context.Logger.Error("无法取得插件输入服务，动态小地图缩放不可用。");
    }

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginSliderSetting
        {
            Key = SensitivityKey,
            DisplayName = "缩放灵敏度（%）",
            Description = "每个滚轮刻度的缩放幅度；50% 为默认，数值越高缩放越快。",
            Minimum = DynamicMiniMapZoomPolicy.MinimumSensitivityPercent,
            Maximum = DynamicMiniMapZoomPolicy.MaximumSensitivityPercent,
            StepFrequency = 5d,
            DefaultValue = DynamicMiniMapZoomPolicy.DefaultSensitivityPercent
        },
        new PluginKeyBindingSetting
        {
            Key = BindingKey,
            DisplayName = "小地图缩放辅助键",
            Description = "按住此键并滚动鼠标滚轮调整小地图大小；支持键盘键或鼠标键。",
            DefaultValue = "keyboard:14:0"
        }
    ];

    public override void OnEnable()
    {
        _enabled = true;
        _matchStarted = _session?.IsMatchStarted == true;
        _matchId = _session?.CurrentMatchId;
        _input?.MouseWheelScrolled += OnMouseWheelScrolled;
        ApplyBinding();
    }

    public override void OnDisable()
    {
        _enabled = false;
        if (_input is not null)
            _input.MouseWheelScrolled -= OnMouseWheelScrolled;
        _pluginInput?.ClearBindings(Context.PluginId);
        EndTemporaryMatch();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_input is not null)
            _input.MouseWheelScrolled -= OnMouseWheelScrolled;
        _pluginInput?.ClearBindings(Context.PluginId);
        EndTemporaryMatch();
        _input = null;
        _overlay = null;
        _session = null;
    }

    public object? GetSettingValue(string key) => key switch
    {
        SensitivityKey => _sensitivityPercent,
        BindingKey => _binding.StorageValue,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (key == SensitivityKey && TryReadDouble(value, out var sensitivityPercent))
        {
            _sensitivityPercent = Math.Clamp(
                sensitivityPercent,
                DynamicMiniMapZoomPolicy.MinimumSensitivityPercent,
                DynamicMiniMapZoomPolicy.MaximumSensitivityPercent);
            return;
        }

        if (key != BindingKey
            || value is not string text
            || !PluginInputBinding.TryParse(text, out var binding))
        {
            return;
        }

        _binding = binding;
        if (_enabled)
            ApplyBinding();
    }

    public void Handle(MatchStateChangedMessage message)
    {
        if (string.Equals(message.State, "Started", StringComparison.OrdinalIgnoreCase))
        {
            if (!_matchStarted
                || !string.Equals(_matchId, message.MatchId, StringComparison.OrdinalIgnoreCase))
            {
                EndTemporaryMatch();
                _matchStarted = true;
                _matchId = message.MatchId;
            }

            return;
        }

        if (string.Equals(message.State, "Ended", StringComparison.OrdinalIgnoreCase))
            EndTemporaryMatch();
    }

    private void OnMouseWheelScrolled(object? sender, MouseWheelInputEventArgs args)
    {
        if (!_enabled
            || !_matchStarted
            || !args.IsPluginBindingPressed(Context.PluginId, BindingKey)
            || args.Delta == 0)
            return;

        if (_overlay?.CurrentMiniMapScale is not double currentScale)
            return;

        var nextScale = DynamicMiniMapZoomPolicy.Apply(
            currentScale,
            args.Delta,
            _sensitivityPercent);
        if (Math.Abs(nextScale - currentScale) <= 0.000001d)
            return;

        _overlay.SetMiniMapScale(nextScale);
    }

    private void ApplyBinding() =>
        _pluginInput?.SetBinding(Context.PluginId, BindingKey, _binding);

    private void EndTemporaryMatch()
    {
        _overlay?.ClearTemporaryMiniMapScales();

        _matchStarted = false;
        _matchId = null;
    }

    private static bool TryReadDouble(object? value, out double result)
    {
        result = value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            _ => double.NaN
        };
        return double.IsFinite(result);
    }
}
