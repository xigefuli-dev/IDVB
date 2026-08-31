using IDVBuff.Plugins.DynamicMiniMapZoom;
using IDVBuff.Core.Contracts;
using Xunit;

namespace IDVBuff.Tests;

public sealed class DynamicMiniMapZoomPolicyTests
{
    [Fact]
    public void Apply_ChangesScaleByOneStepPerStandardWheelNotch()
    {
        Assert.Equal(
            0.51d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, DynamicMiniMapZoomPolicy.StandardWheelDelta),
            precision: 10);
        Assert.Equal(
            0.49d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, -DynamicMiniMapZoomPolicy.StandardWheelDelta),
            precision: 10);
    }

    [Fact]
    public void Apply_ClampsToTheSupportedMiniMapRange()
    {
        Assert.Equal(
            DynamicMiniMapZoomPolicy.MaximumScale,
            DynamicMiniMapZoomPolicy.Apply(0.99d, 1200));
        Assert.Equal(
            DynamicMiniMapZoomPolicy.MinimumScale,
            DynamicMiniMapZoomPolicy.Apply(0.11d, -1200));
    }

    [Fact]
    public void Apply_SupportsHighResolutionWheelDeltas()
    {
        Assert.Equal(0.5025d, DynamicMiniMapZoomPolicy.Apply(0.50d, 30), precision: 10);
    }

    [Fact]
    public void Apply_UsesConfiguredSensitivityPercent()
    {
        Assert.Equal(
            0.56d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, 120, sensitivityPercent: 300d),
            precision: 10);
        Assert.Equal(
            0.505d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, 120, sensitivityPercent: 25d),
            precision: 10);
    }

    [Fact]
    public void Apply_ClampsSensitivityToTheSupportedRange()
    {
        Assert.Equal(
            0.56d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, 120, sensitivityPercent: 1000d),
            precision: 10);
        Assert.Equal(
            0.505d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, 120, sensitivityPercent: 0d),
            precision: 10);
    }

    [Fact]
    public void WheelInput_PreservesPluginBindingStateCapturedBeforeUiDispatch()
    {
        var input = new MouseWheelInputEventArgs(
            timestamp: 1,
            delta: 120,
            capsHeld: false,
            pluginBindingStates: new HashSet<PluginInputBindingState>
            {
                new("dynamic-minimap-zoom", "wheel-modifier-binding")
            });

        Assert.True(input.IsPluginBindingPressed(
            "dynamic-minimap-zoom", "wheel-modifier-binding"));
        Assert.False(input.IsPluginBindingPressed(
            "dynamic-minimap-zoom", "other-binding"));
    }

    [Fact]
    public void WheelInput_CoalescesContiguousInputWithTheSameHeldBinding()
    {
        var held = new HashSet<PluginInputBindingState>
        {
            new("dynamic-minimap-zoom", "wheel-modifier-binding")
        };
        var first = new MouseWheelInputEventArgs(1, 120, false, held);
        var second = new MouseWheelInputEventArgs(2, 240, false, held);

        var combined = first.Coalesce(second);

        Assert.Equal(360, combined.Delta);
        Assert.Equal(2, combined.Timestamp);
        Assert.True(combined.IsPluginBindingPressed(
            "dynamic-minimap-zoom", "wheel-modifier-binding"));
    }

    [Fact]
    public void WheelInput_DoesNotCoalesceWhenTheHeldBindingChanges()
    {
        var held = new MouseWheelInputEventArgs(
            1, 120, false,
            new HashSet<PluginInputBindingState>
            {
                new("dynamic-minimap-zoom", "wheel-modifier-binding")
            });
        var released = new MouseWheelInputEventArgs(2, 120, false);

        Assert.False(held.CanCoalesceWith(released));
    }
}
