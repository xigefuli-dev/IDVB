using IDVBuff.Features.Maps;

namespace IDVBuff.Features.QuickStart;

/// <summary>Provides the developer-owned settings used by the first-run quick-start flow.</summary>
public static class QuickStartRecommendedSettings
{
    /// <summary>
    /// Creates recommended configuration 1. Values not listed by the product
    /// definition intentionally remain at the normal runtime defaults.
    /// </summary>
    public static MapRuntimeSettings CreateRecommendation1()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        // General
        // The recommended profile must not bypass the first-run setup flow.
        // Users still need to finish bindings and add a map before enabling
        // the runtime explicitly.
        settings.IsEnabled = false;
        settings.FirstScanStrategy = FirstScanStrategy.SideEntrance;
        settings.BackgroundScanEnabled = true;
        settings.EnableContinuousAlignment = false;
        settings.SelectedResolutionPreset = null;
        settings.AllowAutomaticMapCache = false;
        settings.CollectLogs = true;
        // Privacy-scoped collection follows the explicit main-program consent
        // and is applied after this general recommendation.
        settings.CollectAlignmentResearchData = false;
        settings.ContinuousMapLearningEnabled = false;
        settings.ShowOverlayStatus = true;
        settings.AllowMapExtendBeyondBounds = true;
        settings.PersistentMiniMapEnabled = true;

        // Large map
        settings.ShowGateMarkers = true;
        settings.ShowAuxiliaryAnchors = false;
        settings.ShowTextAnnotations = true;
        settings.ShowBoxAnnotations = true;
        settings.ShowLineAnnotations = true;

        // Mini map
        settings.ShowGateMarkersOnMiniMap = false;
        settings.ShowAuxiliaryAnchorsOnMiniMap = false;
        settings.ShowTextAnnotationsOnMiniMap = true;
        settings.ShowBoxAnnotationsOnMiniMap = true;
        settings.ShowLineAnnotationsOnMiniMap = true;
        settings.ShowFloorOnMiniMap = true;

        return settings;
    }
}
