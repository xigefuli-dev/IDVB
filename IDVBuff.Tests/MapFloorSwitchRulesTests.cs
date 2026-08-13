using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapFloorSwitchRulesTests
{
    [Fact]
    public void AlignedIdentityTakesPrecedenceOverPendingIdentity()
    {
        var aligned = new object();
        var pending = new object();

        var resolution = MapFloorIdentityRules.Resolve(aligned, pending);

        Assert.Same(aligned, resolution.Identity);
        Assert.Equal(MapFloorIdentityState.Aligned, resolution.State);
    }

    [Fact]
    public void PendingIdentityRemainsAvailableBeforeFirstAlignment()
    {
        var pending = new object();

        var resolution = MapFloorIdentityRules.Resolve<object>(null, pending);

        Assert.Same(pending, resolution.Identity);
        Assert.Equal(MapFloorIdentityState.PendingAlignment, resolution.State);
    }

    [Fact]
    public void ClearedMatchIdentitiesCannotLeakIntoNextMatch()
    {
        var resolution = MapFloorIdentityRules.Resolve<object>(null, null);

        Assert.Null(resolution.Identity);
        Assert.Equal(MapFloorIdentityState.None, resolution.State);
    }

    [Fact]
    public void NextCyclesFromCurrentFloor()
    {
        var decision = MapFloorSwitchDecision.Next(ThreeFloorMap(), "upper");

        Assert.True(decision.Succeeded);
        Assert.Equal("upper", decision.FromFloorKey);
        Assert.Equal("basement", decision.ToFloorKey);
        Assert.Equal(MapFloorSwitchFailure.None, decision.Failure);
    }

    [Fact]
    public void NextUsesFirstFloorWhenCurrentFloorIsUnknown()
    {
        var decision = MapFloorSwitchDecision.Next(ThreeFloorMap(), "missing");

        Assert.True(decision.Succeeded);
        Assert.Equal("missing", decision.FromFloorKey);
        Assert.Equal("main", decision.ToFloorKey);
    }

    [Fact]
    public void SingleFloorDoesNotPretendToSwitch()
    {
        var map = new MapRecord
        {
            Floors = [new FloorDefinition { Key = "main", SortOrder = 1 }]
        };

        var decision = MapFloorSwitchDecision.Next(map, "main");

        Assert.False(decision.Succeeded);
        Assert.Equal(MapFloorSwitchFailure.NoOtherFloor, decision.Failure);
        Assert.Null(decision.ToFloorKey);
    }

    [Fact]
    public void EmptyFloorListReturnsExplicitFailure()
    {
        var decision = MapFloorSwitchDecision.Next(
            new MapRecord { Floors = [] },
            null);

        Assert.False(decision.Succeeded);
        Assert.Equal(MapFloorSwitchFailure.NoFloors, decision.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void InvalidPositionDoesNotChangeFloor(int position)
    {
        var decision = MapFloorSwitchDecision.AtPosition(
            ThreeFloorMap(),
            "upper",
            position);

        Assert.False(decision.Succeeded);
        Assert.Equal("upper", decision.FromFloorKey);
        Assert.Equal(MapFloorSwitchFailure.InvalidPosition, decision.Failure);
    }

    [Fact]
    public void ExactPositionSelectsRequestedFloor()
    {
        var decision = MapFloorSwitchDecision.AtPosition(
            ThreeFloorMap(),
            "main",
            3);

        Assert.True(decision.Succeeded);
        Assert.Equal("basement", decision.ToFloorKey);
    }

    private static MapRecord ThreeFloorMap() => new()
    {
        Floors =
        [
            new FloorDefinition { Key = "main", SortOrder = 1 },
            new FloorDefinition { Key = "upper", SortOrder = 2 },
            new FloorDefinition { Key = "basement", SortOrder = 3 }
        ]
    };
}
