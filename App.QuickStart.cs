using IDVBuff.Features.Plugins;

namespace IDVBuff;

public partial class App
{
    private async Task ApplyQuickStartSelectionAsync(
        Features.Maps.SessionOrchestrator session)
    {
        await session.ApplyQuickStartRecommendedSettingsAsync();
        await session.SetMapImprovementDataCollectionEnabledAsync(
            Lifecycle.MainProgramPreferences.Load().HelpImproveModels);
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
