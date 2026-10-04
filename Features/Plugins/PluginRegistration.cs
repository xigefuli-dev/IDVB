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
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin"))
            host.Register(new IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin());
        // 对局快捷键：两个可自定义快捷键，各自一键进入 / 结束指定地图模式的对局。
        // 常驻（AlwaysActive）：开局前宿主会关掉非常驻插件的运行时闸门，而它正是用来开局的。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.MatchHotkeys.MatchHotkeysPlugin"))
            host.Register(new IDVBuff.Plugins.MatchHotkeys.MatchHotkeysPlugin());
    }
}
