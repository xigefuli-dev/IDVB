using System.Diagnostics;
using System.Reflection;
using IDVBuff.PluginContracts;
using IDVBuff.Plugins.SceneQuickActions;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class SceneQuickActionsTimingTests
{
    [Fact]
    public void DefaultsAppendThirtyToFiftyMillisecondsToExistingWait()
    {
        var options = new SceneQuickActionsOptions();
        Assert.Equal((30, 50), options.GetOrderedRandomDelayRange());
        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(options.GetDelayAfterStep(150), 180, 200);
            Assert.InRange(options.GetDelayAfterStep(), 30, 50);
        }
    }

    [Theory]
    [InlineData(80, 30, 50, 80)]
    [InlineData(-100, -50, 30, 50)]
    [InlineData(20000, 30000, 10000, 10000)]
    [InlineData(100, 100, 100, 100)]
    public void OrdersAndBoundsTheRange(int minimum, int maximum, int expectedMinimum, int expectedMaximum)
    {
        var options = new SceneQuickActionsOptions
        {
            MinimumRandomDelayMilliseconds = minimum,
            MaximumRandomDelayMilliseconds = maximum
        };
        Assert.Equal((expectedMinimum, expectedMaximum), options.GetOrderedRandomDelayRange());
        Assert.InRange(options.GetDelayAfterStep(300), 300 + expectedMinimum, 300 + expectedMaximum);
    }

    [Fact]
    public void SettingsRoundTripAndDoNotAlterOtherTimings()
    {
        var plugin = new SceneQuickActionsPlugin();
        var minimum = Assert.IsType<PluginSliderSetting>(plugin.Settings.Single(
            setting => setting.Key == SceneQuickActionsOptions.MinimumRandomDelayKey));
        var maximum = Assert.IsType<PluginSliderSetting>(plugin.Settings.Single(
            setting => setting.Key == SceneQuickActionsOptions.MaximumRandomDelayKey));
        Assert.Equal(SceneQuickActionsOptions.TimingGroupTitle, minimum.Group);
        Assert.Equal(30d, minimum.DefaultValue);
        Assert.Equal(50d, maximum.DefaultValue);
        Assert.Equal(0d, minimum.MinimumWhenUnsafe);
        plugin.SetSettingValue(minimum.Key, 125d);
        plugin.SetSettingValue(maximum.Key, 200d);
        Assert.Equal(125d, plugin.GetSettingValue(minimum.Key));
        Assert.Equal(200d, plugin.GetSettingValue(maximum.Key));
        plugin.SetSettingValue(minimum.Key, double.NaN);
        Assert.Equal(125d, plugin.GetSettingValue(minimum.Key));
        Assert.Equal(150d, plugin.GetSettingValue(SceneQuickActionsOptions.WakeDelayKey));
        Assert.Equal(260d, plugin.GetSettingValue(SceneQuickActionsOptions.BetweenClicksKey));
    }

    [Fact]
    public void CancellationInterruptsAnAddedTenSecondDelay()
    {
        var runner = typeof(SceneQuickActionsPlugin).Assembly.GetType(
            "IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsRunner", true)!;
        var wait = runner.GetMethod("SleepAfterStep", BindingFlags.NonPublic | BindingFlags.Static)!;
        var options = new SceneQuickActionsOptions
        {
            MinimumRandomDelayMilliseconds = 10000,
            MaximumRandomDelayMilliseconds = 10000
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);
        var clock = Stopwatch.StartNew();
        var exception = Assert.Throws<TargetInvocationException>(() =>
            wait.Invoke(null, [options, 0, cancellation.Token]));
        Assert.IsType<OperationCanceledException>(exception.InnerException);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }
}
