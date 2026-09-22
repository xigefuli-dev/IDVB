using IDVBuff.Diagnostics;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 插件组合根，与 <see cref="IDVBuff.Modules.ModuleRegistration"/> 平行。
/// 内置与导入插件在此编译时登记。
/// </summary>
public static class PluginRegistration
{
    public static void Register(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // 内置/导入插件在此登记。
        host.Register(new IDVBuff.Plugins.IdvLogin.IdvLoginPlugin
        {
            WaitForHostReady = () => App.MainWindowPresentationCompleted
        });
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.AutoClicker.AutoClickerPlugin"))
            host.Register(new IDVBuff.Plugins.AutoClicker.AutoClickerPlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.NightVision.NightVisionPlugin"))
            host.Register(new IDVBuff.Plugins.NightVision.NightVisionPlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.DynamicMiniMapZoom.DynamicMiniMapZoomPlugin"))
            host.Register(new IDVBuff.Plugins.DynamicMiniMapZoom.DynamicMiniMapZoomPlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.LiveMode.LiveModePlugin"))
            host.Register(new IDVBuff.Plugins.LiveMode.LiveModePlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.CustomPhrases.CustomPhrasePlugin"))
            host.Register(new IDVBuff.Plugins.CustomPhrases.CustomPhrasePlugin());
    }
}
