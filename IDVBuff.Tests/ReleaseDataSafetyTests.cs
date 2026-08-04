using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class ReleaseDataSafetyTests
{
    [Fact]
    public void FirstRunConfigurationIsSafeAndUserConfigurable()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        Assert.False(settings.IsEnabled);
        Assert.Equal(FirstScanStrategy.DoubleGate, settings.FirstScanStrategy);
        Assert.False(settings.CollectLogs);
        Assert.False(settings.CollectAlignmentResearchData);
        Assert.All(AllBindings(settings), binding => Assert.False(binding.IsConfigured));
    }

    [Fact]
    public void FirstRunConfigurationContainsNoDeveloperOrDeviceState()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        var json = JsonSerializer.Serialize(settings);

        Assert.Null(settings.SelectedMapId);
        Assert.Empty(settings.AlignmentCalibrations);
        Assert.Empty(settings.FloorScaleCalibrations);
        Assert.Null(settings.MapViewportRegion);
        Assert.Equal(0, settings.CalibrationClientWidth);
        Assert.Equal(0, settings.CalibrationClientHeight);
        Assert.Null(settings.FloorDisplayRegion);
        Assert.Equal(0, settings.FloorCalibrationClientWidth);
        Assert.Equal(0, settings.FloorCalibrationClientHeight);
        Assert.DoesNotContain("1920", json);
        Assert.DoesNotContain("2560", json);
        Assert.DoesNotContain("3840", json);
        Assert.DoesNotContain("Dpi", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewSettingsRepositoryUsesTheSafeFirstRunConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"idvb-safe-default-{Guid.NewGuid():N}");
        try
        {
            var settings = await new MapRuntimeSettingsRepository(root).LoadAsync();

            Assert.False(settings.IsEnabled);
            Assert.False(settings.CollectLogs);
            Assert.False(settings.CollectAlignmentResearchData);
            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<MapInputBinding> AllBindings(MapRuntimeSettings settings) =>
    [
        settings.QuickScanBinding,
        settings.OverlayToggleBinding,
        settings.GameMapToggleBinding,
        settings.ControlPanelToggleBinding,
        settings.ManualRecognitionBinding,
        settings.SwitchFloorBinding
    ];
}
