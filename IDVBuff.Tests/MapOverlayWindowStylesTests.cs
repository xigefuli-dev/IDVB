using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOverlayWindowStylesTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(MapOverlayWindowStyles.Layered)]
    [InlineData(MapOverlayWindowStyles.NoRedirectionBitmap)]
    [InlineData(MapOverlayWindowStyles.Layered | MapOverlayWindowStyles.NoRedirectionBitmap)]
    public void Create_SelectsLayeredRenderingAndRequiredInputStyles(long currentStyles)
    {
        var result = MapOverlayWindowStyles.Create(currentStyles);

        Assert.True(MapOverlayWindowStyles.AreApplied(result));
        Assert.NotEqual(0, result & MapOverlayWindowStyles.Layered);
        Assert.Equal(0, result & MapOverlayWindowStyles.NoRedirectionBitmap);
    }

    [Fact]
    public void AreApplied_RejectsIncompleteInputStyles()
    {
        Assert.False(MapOverlayWindowStyles.AreApplied(MapOverlayWindowStyles.Transparent));
        Assert.False(MapOverlayWindowStyles.AreApplied(
            MapOverlayWindowStyles.Transparent | MapOverlayWindowStyles.ToolWindow));
        Assert.False(MapOverlayWindowStyles.AreApplied(
            MapOverlayWindowStyles.Required | MapOverlayWindowStyles.NoRedirectionBitmap));
    }
}
