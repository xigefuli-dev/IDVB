using IDVBuff.Plugins.DynamicMiniMapZoom;
using Xunit;

namespace IDVBuff.Tests;

public sealed class DynamicMiniMapZoomPolicyTests
{
    [Fact]
    public void Apply_ChangesScaleByOneStepPerStandardWheelNotch()
    {
        Assert.Equal(
            0.52d,
            DynamicMiniMapZoomPolicy.Apply(0.50d, DynamicMiniMapZoomPolicy.StandardWheelDelta),
            precision: 10);
        Assert.Equal(
            0.48d,
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
        Assert.Equal(0.505d, DynamicMiniMapZoomPolicy.Apply(0.50d, 30), precision: 10);
    }
}
