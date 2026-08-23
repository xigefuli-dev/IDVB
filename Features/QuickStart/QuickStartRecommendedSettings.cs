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
        settings.IsEnabled = true;
        settings.FirstScanStrategy = FirstScanStrategy.SideEntrance;
        settings.BackgroundScanEnabled = true;
        settings.SelectedResolutionPreset = null;
        settings.AllowAutomaticMapCache = true;
        settings.CollectLogs = true;
        settings.CollectAlignmentResearchData = true;
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
