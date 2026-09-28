using IDVBuff.Features.QuickStart;
using IDVBuff.Features.Maps;
using IDVBuff.Lifecycle;

namespace IDVBuff.Tests;

public sealed class QuickStartTests
{
    [Fact]
    public void NewDataDirectoryShowsQuickStartUntilItIsCompleted()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new QuickStartStateStore(root);

            Assert.True(store.ShouldShow);

            store.MarkCompleted();

            Assert.False(store.ShouldShow);
            Assert.True(File.Exists(store.StatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeSettingsDoNotSuppressUncompletedQuickStart()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsDirectory = Path.Combine(root, "MapRuntime");
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{}");

            var store = new QuickStartStateStore(root);
            Assert.True(store.ShouldShow);
            store.MarkCompleted();
            Assert.False(store.ShouldShow);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecommendationOneUsesRequestedValuesAndDefaultsForTheRest()
    {
        var recommended = QuickStartRecommendedSettings.CreateRecommendation1();

        Assert.Equal(MapRuntimeSettings.CurrentSchemaVersion, recommended.SchemaVersion);
        Assert.False(recommended.IsEnabled);
        Assert.Equal(FirstScanStrategy.SideEntrance, recommended.FirstScanStrategy);
        Assert.False(recommended.BackgroundScanEnabled);
        Assert.False(recommended.EnableContinuousAlignment);
        Assert.Null(recommended.SelectedResolutionPreset);
        Assert.False(recommended.AllowAutomaticMapCache);
        Assert.True(recommended.CollectLogs);
        Assert.False(recommended.CollectAlignmentResearchData);
        Assert.False(recommended.ContinuousMapLearningEnabled);
        Assert.True(recommended.ShowOverlayStatus);
        Assert.True(recommended.AllowMapExtendBeyondBounds);
        Assert.True(recommended.PersistentMiniMapEnabled);
        recommended.Normalize();
        Assert.Equal(1d, recommended.StatusOffsetY);
        Assert.Equal(1d, recommended.MiniMapOffsetY);

        Assert.True(recommended.ShowRoutes);
        Assert.True(recommended.ShowGateMarkers);
        Assert.False(recommended.ShowAuxiliaryAnchors);
        Assert.True(recommended.ShowTextAnnotations);
        Assert.True(recommended.ShowBoxAnnotations);
        Assert.True(recommended.ShowLineAnnotations);

        Assert.False(recommended.ShowGateMarkersOnMiniMap);
        Assert.False(recommended.ShowAuxiliaryAnchorsOnMiniMap);
        Assert.True(recommended.ShowTextAnnotationsOnMiniMap);
        Assert.True(recommended.ShowBoxAnnotationsOnMiniMap);
        Assert.True(recommended.ShowLineAnnotationsOnMiniMap);
        Assert.True(recommended.ShowFloorOnMiniMap);

        // A setting not listed by recommendation 1 keeps its default value.
        Assert.False(recommended.PlayerTrackingEnabled);
    }

    [Fact]
    public void RecommendationOneDisablesMinimizeToTrayInMainProgramPreferences()
    {
        var preferences = new MainProgramPreferences { MinimizeToTray = true };
        QuickStartRecommendedSettings.ApplyRecommendation1(preferences);

        Assert.False(preferences.MinimizeToTray);

        var created = QuickStartRecommendedSettings.CreateRecommendedPreferences();
        Assert.False(created.MinimizeToTray);
    }

    [Fact]
    public void MainProgramPreferences_MinimizeToTray_DefaultsToFalse()
    {
        var preferences = new MainProgramPreferences();
        Assert.False(preferences.MinimizeToTray);
    }

    [Fact]
    public void MainProgramPreferences_EnhancedMiniMapEnabled_DefaultsToFalse()
    {
        var preferences = new MainProgramPreferences();
        Assert.False(preferences.EnhancedMiniMapEnabled);
    }

    [Fact]
    public void MainProgramPreferences_DisableScaleLocking_DefaultsToFalse()
    {
        var preferences = new MainProgramPreferences();
        Assert.False(preferences.DisableScaleLocking);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IDVB-QuickStart-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
