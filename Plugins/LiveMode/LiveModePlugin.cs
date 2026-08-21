using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.LiveMode;

/// <summary>直播模式：统一控制 IDVB 窗口是否从系统捕获中排除。</summary>
[Plugin(
    "live-mode",
    DisplayName = "直播模式",
    Description = "控制 Identity Vision Bridge 主程序和显示层是否从录屏、截图中排除。",
    Version = "1.0.0")]
public sealed class LiveModePlugin : PluginBase, IPluginSettingsProvider
{
    public const string HideMainProgramKey = "hide-main-program";
    public const string HideDisplayLayerKey = "hide-display-layer";

    private bool _hideMainProgram;
    private bool _hideDisplayLayer = true;
    private bool _enabled;
    private ICaptureProtectionService? _captureProtection;

    public override string Id => "live-mode";

    public override string DisplayName => "直播模式";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginToggleSetting
        {
            Key = HideMainProgramKey,
            DisplayName = "隐藏主程序",
            Description = "从系统录屏和截图中排除 Identity Vision Bridge 主窗口。",
            DefaultValue = false
        },
        new PluginToggleSetting
        {
            Key = HideDisplayLayerKey,
            DisplayName = "隐藏显示层",
            Description = "从系统录屏和截图中排除地图、状态和其他辅助视觉窗口。",
            DefaultValue = true
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _captureProtection = context.GetService<ICaptureProtectionService>();
        if (_captureProtection is null)
            context.Logger.Error("无法取得宿主捕获保护服务，直播模式不可用。");
    }

    public override void OnEnable()
    {
        _enabled = true;
        ApplyPolicy();
    }

    public override void OnDisable()
    {
        _enabled = false;
        // 总门控关闭时无条件恢复两类窗口的正常捕获行为。
        _captureProtection?.SetPolicy(
            pluginEnabled: false,
            hideMainProgram: false,
            hideDisplayLayer: false);
    }

    public override void OnUnload()
    {
        if (_enabled)
            OnDisable();
        _captureProtection = null;
    }

    public object? GetSettingValue(string key) => key switch
    {
        HideMainProgramKey => _hideMainProgram,
        HideDisplayLayerKey => _hideDisplayLayer,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        if (value is not bool enabled)
            return;
        switch (key)
        {
            case HideMainProgramKey:
                _hideMainProgram = enabled;
                break;
            case HideDisplayLayerKey:
                _hideDisplayLayer = enabled;
                break;
            default:
                return;
        }

        if (_enabled)
            ApplyPolicy();
    }

    private void ApplyPolicy()
    {
        if (_captureProtection is null)
            return;
        try
        {
            _captureProtection.SetPolicy(
                pluginEnabled: true,
                hideMainProgram: _hideMainProgram,
                hideDisplayLayer: _hideDisplayLayer);
        }
        catch (Exception exception)
        {
            Context.Logger.Error($"直播模式策略应用失败：{exception.Message}");
        }
    }
}
