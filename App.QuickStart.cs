using IDVBuff.Features.Plugins;
using IDVBuff.Features.QuickStart;

namespace IDVBuff;

public partial class App
{
    private async Task ApplyQuickStartSelectionAsync(
        Features.Maps.SessionOrchestrator session)
    {
        await session.ApplyQuickStartRecommendedSettingsAsync();
        if (IsApplicationStopping) return;
        var preferences = Lifecycle.MainProgramPreferences.Load();
        QuickStartRecommendedSettings.ApplyRecommendation1(preferences);
        preferences.Save();
        await session.SetMapImprovementDataCollectionEnabledAsync(
            preferences.HelpImproveModels);
        if (IsApplicationStopping) return;
        DisableBuiltInPluginsForQuickStart();
        if (_thirdPartyPluginRuntime is not null)
            await _thirdPartyPluginRuntime.DisableAllAsync();
    }

    private void DisableBuiltInPluginsForQuickStart()
    {
        if (_pluginManager is not { } pluginManager)
            return;

        foreach (var plugin in pluginManager.Plugins)
        {
            if (pluginManager.IsEnabled(plugin.Id))
                pluginManager.SetEnabled(plugin.Id, enabled: false);
        }
    }
}
