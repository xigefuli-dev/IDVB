using IDVBuff.PluginContracts;
using IDVBuff.Plugins.SceneQuickActions;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class SceneQuickActionsDropPlanTests
{
    [Theory]
    [InlineData(1920, 1080, false)]
    [InlineData(2560, 1440, false)]
    [InlineData(1280, 720, false)]
    [InlineData(1920, 1200, true)]
    [InlineData(2560, 1600, true)]
    [InlineData(1280, 800, true)]
    public void UsesBuiltInSlotsInOrderAtEverySupportedResolution(int width, int height, bool tall)
    {
        var expected = tall ? PluginInventoryScale.AspectRatio16By10 : PluginInventoryScale.AspectRatio16By9;
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(width, height, true, 0, out var bag));
        PluginInventoryCoordinate[] expectedBag = tall
            ? [new(1, 0.2216, 0.329), new(1, 0.2931, 0.334), new(1, 0.3646, 0.3358),
               new(2, 0.2216, 0.474), new(2, 0.3001, 0.474), new(2, 0.3728, 0.4659)]
            : [new(1, 0.2248, 0.2597), new(1, 0.2953, 0.2579), new(1, 0.3657, 0.2597),
               new(2, 0.2238, 0.4137), new(2, 0.2942, 0.4156), new(2, 0.3688, 0.4137)];
        Assert.Equal(expectedBag, bag);
        Assert.Equal(6, bag.Length);
        for (var slot = 0; slot < 4; slot++)
        {
            Assert.True(SceneQuickActionsDropPlan.TryGetSlots(width, height, false, slot, out var hotbar));
            Assert.Equal(expected.Where(item => item.Shape == 3).ElementAt(slot), Assert.Single(hotbar));
        }
    }

    [Theory]
    [InlineData(1920, 1080, 432, 280)]
    [InlineData(1920, 1200, 425, 395)]
    [InlineData(2560, 1600, 567, 526)]
    public void FirstBagSlotMapsToExpectedClientPixel(int width, int height, int x, int y)
    {
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(width, height, true, 0, out var slots));
        Assert.Equal(x, (int)Math.Round(slots[0].X * width));
        Assert.Equal(y, (int)Math.Round(slots[0].Y * height));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1920, -1080, 0)]
    [InlineData(1920, 1201, 0)]
    [InlineData(2560, 1080, 0)]
    [InlineData(1920, 1080, -1)]
    [InlineData(1920, 1080, 4)]
    public void InvalidLayoutDoesNotProduceDragCoordinates(int width, int height, int slot)
    {
        Assert.False(SceneQuickActionsDropPlan.TryGetSlots(width, height, false, slot, out var slots));
        Assert.Empty(slots);
    }

    [Fact]
    public void SwitchingResolutionAndHotbarSlotsKeepsPriorWorkUnchanged()
    {
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(1920, 1080, false, 0, out var first));
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(2560, 1600, false, 3, out var second));
        Assert.Equal(new PluginInventoryCoordinate(3, 0.39, 0.92), Assert.Single(first));
        Assert.Equal(new PluginInventoryCoordinate(3, 0.64, 0.93), Assert.Single(second));
    }

    [Fact]
    public void CallerCannotOverwriteCachedJsonCoordinates()
    {
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(1920, 1080, true, 0, out var first));
        first[0] = default;
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(1280, 800, true, 0, out var tall));
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(2560, 1440, true, 0, out var wide));
        Assert.Equal(new PluginInventoryCoordinate(1, 0.2216, 0.329), tall[0]);
        Assert.Equal(new PluginInventoryCoordinate(1, 0.2248, 0.2597), wide[0]);
    }
}
