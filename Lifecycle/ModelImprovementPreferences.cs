using IDVBuff.Features.Maps;

namespace IDVBuff.Lifecycle;

public static class ModelImprovementPreferences
{
    public static async Task ApplyDataCollectionAsync(
        bool enabled,
        SessionOrchestrator? session = null)
    {
        if (session is not null)
        {
            await session.SetMapImprovementDataCollectionEnabledAsync(enabled);
            return;
        }

        var repository = new MapRuntimeSettingsRepository();
        var settings = await repository.LoadAsync();
        settings.ContinuousMapLearningEnabled = enabled;
        settings.CollectAlignmentResearchData = enabled;
        await repository.SaveAsync(settings);
    }
}
