using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapPlayerModelsTests
{
    [Fact]
    public void MatchStartsWithoutPlayerSlotAndResetsIdentityWhenEnded()
    {
        var session = new MapMatchSession();

        var started = session.Begin("S1");

        Assert.True(started.IsStarted);
        Assert.Null(started.PlayerSlot);

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
        session.Begin("S1");

        Assert.Throws<InvalidOperationException>(
            () => session.Begin("S1"));
    }

    [Fact]
    public void MatchRetainsSelectedMapClassAsPartOfItsIdentity()
    {
        var session = new MapMatchSession();

        var started = session.Begin("Ranked");

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
