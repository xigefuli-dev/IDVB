using IDVBuff.PluginContracts;
using IDVBuff.Plugins.AutoGatling;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class AutoGatlingPlanTests
{
    [Fact]
    public void Options_UseExpectedTimingDefaultsAndOrderRandomRange()
    {
        var options = new AutoGatlingOptions();

        Assert.Equal(50, options.StandardDelayMilliseconds);
        Assert.Equal(2000, options.ReloadDelayMilliseconds);
        Assert.Equal(10, options.KeyPressDelayMilliseconds);
        Assert.Equal(50, options.DragDelayMilliseconds);
        Assert.Equal((10, 20), options.GetOrderedRandomDelayRange());

        options.MinimumRandomDelayMilliseconds = 80;
        options.MaximumRandomDelayMilliseconds = 30;

        Assert.Equal((30, 80), options.GetOrderedRandomDelayRange());
    }

    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(2560, 1440, true)]
    [InlineData(2560, 1600, true)]
    [InlineData(1920, 1200, true)]
    [InlineData(1920, 1201, false)]
    [InlineData(2560, 1080, false)]
    public void TryGetCoordinates_OnlyAcceptsExactSupportedRatios(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoGatlingPlan.TryGetCoordinates(width, height, out _));
    }

    [Fact]
    public void GetInventorySlot_MapsSlotsToShape1ThenShape2()
    {
        var coordinates = PluginInventoryScale.AspectRatio16By9;

        Assert.Equal(new PluginInventoryCoordinate(1, 0.22, 0.59),
            AutoGatlingPlan.GetInventorySlot(coordinates, 1));
        Assert.Equal(new PluginInventoryCoordinate(1, 0.37, 0.59),
            AutoGatlingPlan.GetInventorySlot(coordinates, 3));
        Assert.Equal(new PluginInventoryCoordinate(2, 0.22, 0.72),
            AutoGatlingPlan.GetInventorySlot(coordinates, 4));
        Assert.Equal(new PluginInventoryCoordinate(2, 0.36, 0.72),
            AutoGatlingPlan.GetInventorySlot(coordinates, 6));
        Assert.Equal(new PluginInventoryCoordinate(3, 0.39, 0.92),
            AutoGatlingPlan.GetHotbarSlot(coordinates));
    }

    [Fact]
    public void AutoGatlingPlugin_ExposesThreeKeyboardBindingsWithExpectedDefaults()
    {
        var plugin = new AutoGatlingPlugin();

        Assert.Equal(
            [
                "inventory-binding",
                "activate-binding",
                "reload-binding",
                "standard-delay-ms",
                "reload-delay-ms",
                "key-press-delay-ms",
                "drag-delay-ms",
                "minimum-random-delay-ms",
                "maximum-random-delay-ms"
            ],
            plugin.Settings.Select(setting => setting.Key).ToArray());
        Assert.Equal("keyboard:9:0", plugin.GetSettingValue("inventory-binding"));
        Assert.Equal("keyboard:54:0", plugin.GetSettingValue("activate-binding"));
        Assert.Equal("keyboard:59:0", plugin.GetSettingValue("reload-binding"));

        plugin.SetSettingValue("activate-binding", "none");
        plugin.SetSettingValue("reload-binding", "keyboard:54:3");
        plugin.SetSettingValue("standard-delay-ms", 75d);
        plugin.SetSettingValue("minimum-random-delay-ms", 40d);
        plugin.SetSettingValue("maximum-random-delay-ms", 15d);

        Assert.Equal("none", plugin.GetSettingValue("activate-binding"));
        Assert.Equal("keyboard:54:3", plugin.GetSettingValue("reload-binding"));
        Assert.Equal(75d, plugin.GetSettingValue("standard-delay-ms"));
        Assert.Equal(40d, plugin.GetSettingValue("minimum-random-delay-ms"));
        Assert.Equal(15d, plugin.GetSettingValue("maximum-random-delay-ms"));
    }
}
