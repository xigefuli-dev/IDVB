using IDVBuff.PluginContracts;
using IDVBuff.Plugins.NoRecoveryDelay;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class NoRecoveryDelayPlanTests
{
    [Fact]
    public void Plan_MapsMergedInventoryAndAllEquipmentSlots()
    {
        var coordinates = PluginInventoryScale.AspectRatio16By9;
        Assert.Equal(new PluginInventoryCoordinate(1, 0.22, 0.59), NoRecoveryDelayPlan.GetInventorySlot(coordinates, 1));
        Assert.Equal(new PluginInventoryCoordinate(2, 0.36, 0.72), NoRecoveryDelayPlan.GetInventorySlot(coordinates, 6));
        Assert.Equal(new PluginInventoryCoordinate(3, 0.64, 0.92), NoRecoveryDelayPlan.GetEquipmentSlot(coordinates, 4));
    }

    [Fact]
    public void Plugin_ExposesRequiredSettingsAndRejectsDuplicateInventorySlots()
    {
        var plugin = new NoRecoveryDelayPlugin();
        Assert.Equal(12, plugin.Settings.Count);
        var loopCount = Assert.IsType<PluginSliderSetting>(plugin.Settings.Single(x => x.Key == "loop-count"));
        Assert.Equal((1d, 20d), (loopCount.Minimum, loopCount.Maximum));
        Assert.Equal("loop-mode", loopCount.VisibleWhenKey);
        Assert.Equal("轮次循环", loopCount.VisibleWhenValue);

        plugin.SetSettingValue("inventory-slot-1", "2");
        Assert.Equal("1", plugin.GetSettingValue("inventory-slot-1"));
        plugin.SetSettingValue("inventory-slot-1", "3");
        plugin.SetSettingValue("inventory-slot-2", "3");
        Assert.Equal("3", plugin.GetSettingValue("inventory-slot-1"));
        Assert.Equal("2", plugin.GetSettingValue("inventory-slot-2"));
    }

    [Fact]
    public void Options_ApplyRequiredDelayFloors()
    {
        var plugin = new NoRecoveryDelayPlugin();
        plugin.SetSettingValue("standard-delay-ms", 0d);
        plugin.SetSettingValue("key-press-delay-ms", 0d);
        plugin.SetSettingValue("drag-delay-ms", 0d);
        plugin.SetSettingValue("minimum-random-delay-ms", 0d);
        plugin.SetSettingValue("maximum-random-delay-ms", 0d);
        Assert.Equal(1d, plugin.GetSettingValue("standard-delay-ms"));
        Assert.Equal(10d, plugin.GetSettingValue("key-press-delay-ms"));
        Assert.Equal(25d, plugin.GetSettingValue("drag-delay-ms"));
        Assert.Equal(30d, plugin.GetSettingValue("minimum-random-delay-ms"));
        Assert.Equal(50d, plugin.GetSettingValue("maximum-random-delay-ms"));
    }
}
