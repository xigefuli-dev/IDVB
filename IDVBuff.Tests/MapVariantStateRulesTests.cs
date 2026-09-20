using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapVariantStateRulesTests
{
    [Fact]
    public void VariantEpochInvalidatesOldMapLeaseAndPublishesPendingTarget()
    {
        var matches = new MapMatchSession();
        var oldMatch = matches.Begin("S1");
        var mapA = Guid.NewGuid();
        var mapB = Guid.NewGuid();
        var lease = new MapMatchMapLease();
        lease.Bind(oldMatch, mapA);
        Assert.True(lease.IsCurrent(oldMatch, mapA));

        var newMatch = matches.AdvanceOperationEpoch();
        Assert.False(lease.IsCurrent(newMatch, mapA));
        lease.Bind(newMatch, mapB);
        Assert.True(lease.IsCurrent(newMatch, mapB));
        Assert.False(matches.IsCurrent(oldMatch));

        var session = new MapOpenSession();
        var pending = session.BeginVariantChange(mapB, "2f");
        Assert.Equal(mapB, pending.MapId);
        Assert.Equal("2f", pending.Floor);
        Assert.False(pending.IsLocked);
        Assert.Equal(MapSessionState.RecalibrationRequired, pending.State);
        Assert.Equal(MapRecalibrationReason.VariantChanged, pending.RecalibrationReason);
    }

    [Fact]
    public void TargetBecomesLockedOnlyAfterItsOwnValidTransform()
    {
        var mapB = Guid.NewGuid();
        var session = new MapOpenSession();
        session.BeginVariantChange(mapB, "1f");
        var transform = new MapSimilarityTransform
        {
            Scale = 1.1,
            RotationDegrees = 0,
            TranslationX = 25,
            TranslationY = 30
        };

        var locked = session.LockAlignedMap(
            mapB,
            "1f",
            transform,
            MapLocationMethod.StructureTranslation,
            0.9);

        Assert.True(locked.IsLocked);
        Assert.Equal(mapB, locked.MapId);
        Assert.Equal(transform, locked.LockedTransform);
    }

    [Fact]
    public void PendingVariantFloorRetargetsImmediatelyWithoutBecomingLocked()
    {
        var mapB = Guid.NewGuid();
        var session = new MapOpenSession();
        session.BeginVariantChange(mapB, "1f");

        var retargeted = session.RetargetVariantFloor(mapB, "2f");

        Assert.Equal(mapB, retargeted.MapId);
        Assert.Equal("2f", retargeted.Floor);
        Assert.False(retargeted.IsLocked);
        Assert.Equal(MapSessionState.RecalibrationRequired, retargeted.State);
        Assert.Equal(MapRecalibrationReason.VariantChanged, retargeted.RecalibrationReason);
        Assert.Null(retargeted.LockedTransform);
    }

    [Fact]
    public void PendingVariantFloorCannotRetargetAnotherMap()
    {
        var mapB = Guid.NewGuid();
        var session = new MapOpenSession();
        session.BeginVariantChange(mapB, "1f");

        Assert.Throws<InvalidOperationException>(() =>
            session.RetargetVariantFloor(Guid.NewGuid(), "2f"));
    }
}
