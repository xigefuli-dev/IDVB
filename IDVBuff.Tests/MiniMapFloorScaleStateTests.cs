using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MiniMapFloorScaleStateTests
{
    [Fact]
    public void DynamicScalesRemainIndependentForEveryFloor()
    {
        var state = new MiniMapFloorScaleState();
        const string firstFloor = @"C:\maps\example\floor-1-overlay.png";
        const string secondFloor = @"C:\maps\example\floor-2-overlay.png";

        Assert.Equal(0.25d, state.Resolve(firstFloor, 0.25d));
        state.Remember(firstFloor, 0.44d);

        Assert.Equal(0.25d, state.Resolve(secondFloor, 0.25d));
        state.Remember(secondFloor, 0.68d);

        Assert.Equal(0.44d, state.Resolve(firstFloor, 0.25d));
        Assert.Equal(0.68d, state.Resolve(secondFloor, 0.25d));
    }

    [Fact]
    public void EndingMatchClearsEveryFloorWithoutCrossFloorFallback()
    {
        var state = new MiniMapFloorScaleState();
        state.Remember("floor-1", 0.44d);
        state.Remember("floor-2", 0.68d);

        state.Clear();

        Assert.Equal(0.25d, state.Resolve("floor-1", 0.25d));
        Assert.Equal(0.25d, state.Resolve("floor-2", 0.25d));
    }
}
