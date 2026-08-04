using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapPlayerModelsTests
{
    [Fact]
    public void MatchRequiresAValidPlayerAndResetsIdentityWhenEnded()
    {
        var session = new MapMatchSession();

        var started = session.Begin(PlayerSlot.Player3);

        Assert.True(started.IsStarted);
        Assert.Equal(PlayerSlot.Player3, started.PlayerSlot);

        var ended = session.End();

        Assert.Equal(MapMatchState.Ended, ended.State);
        Assert.Null(ended.PlayerSlot);
        Assert.False(session.IsCurrent(started));
        Assert.True(session.IsCurrent(ended));
    }

    [Fact]
    public void MatchCannotBeStartedTwice()
    {
        var session = new MapMatchSession();
        session.Begin(PlayerSlot.Player1);

        Assert.Throws<InvalidOperationException>(
            () => session.Begin(PlayerSlot.Player2));
    }

    [Fact]
    public void MatchRetainsSelectedMapClassAsPartOfItsIdentity()
    {
        var session = new MapMatchSession();

        var started = session.Begin(PlayerSlot.Player2, "Ranked");

        Assert.Equal("Ranked", started.MapClass);
        Assert.True(session.IsCurrent(started));
        Assert.False(session.IsCurrent(started with { MapClass = "Quick" }));
    }

    [Fact]
    public void EveryPlayerSlotHasAnAvailablePackagedAsset()
    {
        Assert.True(MapPlayerAssetCatalog.AreAllAvailable);
        Assert.Equal(4, MapPlayerAssetCatalog.Slots.Count);
        foreach (var slot in MapPlayerAssetCatalog.Slots)
        {
            Assert.True(File.Exists(MapPlayerAssetCatalog.ResolvePath(slot)));
        }
    }
}
