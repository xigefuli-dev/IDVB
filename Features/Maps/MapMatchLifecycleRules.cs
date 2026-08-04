namespace IDVBuff.Features.Maps;

public enum MapAlignmentPrerequisiteKind
{
    DoubleGateInitialScan,
    SideEntranceInitialScan,
    DefaultDualGateAlignment,
    DefaultSingleGateAlignment,
    DefaultStructureAlignment,
    SideDualGateAlignment,
    SideSingleGateAlignment,
    SideStructureAlignment,
    OtherFloorStructureAlignment
}

/// <summary>
/// A selected map is valid only inside the match version that selected it.
/// Persisted settings are not sufficient proof that a later match may reuse
/// the map, alignment seed, or any tracking observation from that match.
/// </summary>
public sealed class MapMatchMapLease
{
    public Guid? MapId { get; private set; }
    public int MatchVersion { get; private set; }

    public void Bind(MapMatchSnapshot match, Guid mapId)
    {
        if (!match.IsStarted)
            throw new InvalidOperationException("A map can be selected only for an active match.");
        if (mapId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(mapId));

        MapId = mapId;
        MatchVersion = match.Version;
    }

    public bool IsCurrent(MapMatchSnapshot match, Guid mapId) =>
        match.IsStarted
        && MapId == mapId
        && MatchVersion == match.Version;

    public void Clear()
    {
        MapId = null;
        MatchVersion = 0;
    }
}

public static class MapMatchLifecycleRules
{
    public static MapRuntimeSettings CreateSettingsWithoutMatchSelection(
        MapRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cleared = settings.Clone();
        cleared.SelectedMapId = null;
        return cleared;
    }

    public static bool CanStart(
        MapAlignmentPrerequisiteKind operation,
        MapMatchSnapshot currentMatch,
        MapMatchSnapshot operationMatch,
        FirstScanStrategy configuredStrategy,
        MapMatchMapLease mapLease,
        Guid? selectedMapId = null,
        MapAlignmentSession? alignmentSession = null,
        MapOverlayTransform? floorScaleSeed = null)
    {
        ArgumentNullException.ThrowIfNull(mapLease);
        if (!currentMatch.IsStarted
            || currentMatch.Version != operationMatch.Version
            || currentMatch.State != operationMatch.State
            || currentMatch.PlayerSlot != operationMatch.PlayerSlot
            || !string.Equals(
                currentMatch.MapClass,
                operationMatch.MapClass,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (operation is MapAlignmentPrerequisiteKind.DoubleGateInitialScan
            or MapAlignmentPrerequisiteKind.SideEntranceInitialScan)
        {
            var expectedStrategy = operation
                == MapAlignmentPrerequisiteKind.DoubleGateInitialScan
                    ? FirstScanStrategy.DoubleGate
                    : FirstScanStrategy.SideEntrance;
            return configuredStrategy == expectedStrategy
                && mapLease.MapId is null
                && selectedMapId is null
                && alignmentSession is null;
        }

        if (selectedMapId is not { } mapId
            || !mapLease.IsCurrent(currentMatch, mapId))
        {
            return false;
        }

        if (operation
            == MapAlignmentPrerequisiteKind.OtherFloorStructureAlignment)
        {
            return IsValidTransform(floorScaleSeed);
        }

        if (operation is MapAlignmentPrerequisiteKind.DefaultDualGateAlignment
            or MapAlignmentPrerequisiteKind.SideDualGateAlignment)
        {
            return configuredStrategy ==
                (operation == MapAlignmentPrerequisiteKind.DefaultDualGateAlignment
                    ? FirstScanStrategy.DoubleGate
                    : FirstScanStrategy.SideEntrance);
        }

        if (alignmentSession is null
            || alignmentSession.MapId != mapId
            || !IsValidTransform(alignmentSession.LockedTransform))
        {
            return false;
        }

        var isDefaultStrategy = operation is
            MapAlignmentPrerequisiteKind.DefaultDualGateAlignment
            or MapAlignmentPrerequisiteKind.DefaultSingleGateAlignment
            or MapAlignmentPrerequisiteKind.DefaultStructureAlignment;
        if (isDefaultStrategy)
        {
            return configuredStrategy == FirstScanStrategy.DoubleGate
                && alignmentSession.HasGatePairLock;
        }

        return configuredStrategy == FirstScanStrategy.SideEntrance
            && (alignmentSession.SideEntranceScanPriorConfidence > 0d
                || alignmentSession.HasGatePairLock);
    }

    private static bool IsValidTransform(MapOverlayTransform? transform) =>
        transform is not null
        && double.IsFinite(transform.ScaleX)
        && double.IsFinite(transform.ScaleY)
        && transform.ScaleX > 0.05d
        && transform.ScaleY > 0.05d
        && transform.ReferenceWidth > 0
        && transform.ReferenceHeight > 0;
}
