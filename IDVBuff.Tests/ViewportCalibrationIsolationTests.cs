using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class ViewportCalibrationIsolationTests
{
    [Fact]
    public void NativeMiniMapCalibration_SurvivesPersistenceWithoutChangingOtherRegions()
    {
        var settings = new MapRuntimeSettings();
        var region = new NormalizedRectangle { X = 0.02, Y = 0.03, Width = 0.2, Height = 0.2 };
        settings.UpsertNativeMiniMapCalibration(region, 1920, 1080, 96);
        region.X = 0.9;
        var restored = System.Text.Json.JsonSerializer.Deserialize<MapRuntimeSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings.Clone()))!;
        restored.Normalize();
        Assert.Equal(0.02, restored.ResolveNativeMiniMapRegion(1920, 1080)!.X);
        Assert.Null(restored.ResolveNativeMiniMapRegion(2560, 1440));
        Assert.Null(restored.ResolveMapViewportRegion(1920, 1080));
        Assert.Null(restored.ResolveFloorDisplayRegion(1920, 1080));
        Assert.Throws<ArgumentException>(() => settings.UpsertNativeMiniMapCalibration(
            region, 1920, 1080, 96));
        region.X = double.NaN;
        Assert.Throws<ArgumentException>(() => settings.UpsertNativeMiniMapCalibration(
            region, 1920, 1080, 96));
    }

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

    [Fact]
    public void NativeMiniMap_FallbackToDefault_ProvidesUsableRoiWhenUncalibrated()
    {
        var settings = new MapRuntimeSettings();
        Assert.Null(settings.ResolveNativeMiniMapRegion(2560, 1600));

        var fallback1610 = settings.ResolveNativeMiniMapRegion(2560, 1600, fallbackToDefault: true);
        Assert.NotNull(fallback1610);
        Assert.True(fallback1610.IsValid);
        Assert.InRange(fallback1610.X, 0.03, 0.06);
        Assert.InRange(fallback1610.Y, 0.08, 0.12);
        Assert.InRange(fallback1610.Width, 0.04, 0.08);
        Assert.InRange(fallback1610.Height, 0.08, 0.12);

        var fallback169 = settings.ResolveNativeMiniMapRegion(1920, 1080, fallbackToDefault: true);
        Assert.NotNull(fallback169);
        Assert.True(fallback169.IsValid);
        Assert.InRange(fallback169.X, 0.03, 0.05);

        // Explicit calibration takes precedence over fallback
        var custom = new NormalizedRectangle { X = 0.05, Y = 0.05, Width = 0.1, Height = 0.1 };
        settings.UpsertNativeMiniMapCalibration(custom, 2560, 1600, 144);
        var resolved = settings.ResolveNativeMiniMapRegion(2560, 1600, fallbackToDefault: true);
        Assert.NotNull(resolved);
        Assert.Equal(0.05, resolved.X);
    }
}
