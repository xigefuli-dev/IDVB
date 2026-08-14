using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class ViewportCalibrationIsolationTests
{
    [Fact]
    public void ViewportConfig_CarriesOwningClientGeometry()
    {
        var config = new IDVBuff.Core.Models.ViewportCalibrationConfig
        {
            ClientWidth = 2560,
            ClientHeight = 1600
        };

        Assert.Equal(2560, config.ClientWidth);
        Assert.Equal(1600, config.ClientHeight);
    }

    [Fact]
    public void SettingsViewport_DoesNotFallBackAcrossResolutions()
    {
        var settings = new MapRuntimeSettings();
        settings.UpsertMapViewportCalibration(
            new NormalizedRectangle
            {
                X = 0.1,
                Y = 0.1,
                Width = 0.8,
                Height = 0.8
            },
            1920,
            1080,
            120);

        Assert.NotNull(settings.ResolveMapViewportRegion(1920, 1080));
        Assert.Null(settings.ResolveMapViewportRegion(2560, 1600));
    }
}
