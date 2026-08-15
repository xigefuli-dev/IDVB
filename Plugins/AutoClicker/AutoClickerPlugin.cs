using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoClicker;

/// <summary>
/// Right-button hold auto-clicker. After the hold threshold, the physical
/// right button is handed off and complete F down/up events are injected.
/// Releasing the right button stops the service and forces an F key-up.
/// </summary>
[Plugin(
    "auto-clicker",
    DisplayName = "连点器",
    Description = "按住鼠标右键超过 0.1 秒后，以 15ms 周期发送完整 F↓/F↑ 事件，松开即停。",
    Version = "1.2.0")]
public sealed class AutoClickerPlugin : PluginBase
{
    private readonly AutoClickerService _service = new();

    public override string Id => "auto-clicker";

    public override string DisplayName => "连点器";

    public override void OnStart()
    {
        try
        {
            _service.Start();
            Context.Logger.Info(
                $"连点器已启动：按住鼠标右键超过 {AutoClickerPolicy.HoldBeforeClickMilliseconds}ms 后，"
                + "以 15ms 周期发送完整 F↓/F↑ 事件，松开停止。");
        }
        catch (Exception exception)
        {
            // Hook installation failure only disables this plugin; it must not
            // prevent the host application from starting.
            Context.Logger.Error($"连点器启动失败：{exception.Message}");
        }
    }

    public override void OnDisable() => _service.Stop();
}
