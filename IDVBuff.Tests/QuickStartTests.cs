using IDVBuff.Features.QuickStart;
using IDVBuff.Features.Maps;

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
    public void ExistingRuntimeSettingsSuppressFirstRunQuickStart()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsDirectory = Path.Combine(root, "MapRuntime");
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{}");

            Assert.False(new QuickStartStateStore(root).ShouldShow);
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
        Assert.True(recommended.IsEnabled);
        Assert.Equal(FirstScanStrategy.SideEntrance, recommended.FirstScanStrategy);
        Assert.True(recommended.BackgroundScanEnabled);
        Assert.Null(recommended.SelectedResolutionPreset);
        Assert.True(recommended.AllowAutomaticMapCache);
        Assert.True(recommended.CollectLogs);
        Assert.True(recommended.CollectAlignmentResearchData);
        Assert.True(recommended.ShowOverlayStatus);
        Assert.True(recommended.AllowMapExtendBeyondBounds);
        Assert.True(recommended.PersistentMiniMapEnabled);

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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IDVB-QuickStart-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
