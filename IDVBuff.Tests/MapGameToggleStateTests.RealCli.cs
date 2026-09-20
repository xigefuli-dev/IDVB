using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapGameToggleStateRealCliTests
{
    [Fact]
    public void ExternalControllerCanOpenMapAndLeaveAlignmentAvailable()
    {
        var state = new MapGameToggleState();

        var transition = state.SetOpenForExternalController(true);

        Assert.True(state.IsOpen);
        Assert.Equal(state.Version, transition.Version);
        Assert.True(state.TryBeginOpenPipeline(transition));
    }

    [Fact]
    public void ExternalCloseResetsTheOpenPipeline()
    {
        var state = new MapGameToggleState();
        state.SetOpenForExternalController(true);
        state.SetOpenForExternalController(false);

        Assert.False(state.IsOpen);
        Assert.False(state.TryBeginOpenPipeline(
            new MapGameToggleTransition(true, state.Version)));
    }
}
