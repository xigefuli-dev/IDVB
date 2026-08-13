namespace IDVBuff.Features.Maps;

internal enum MapFloorSwitchFailure
{
    None,
    NoFloors,
    NoOtherFloor,
    InvalidPosition
}

internal enum MapFloorIdentityState
{
    None,
    Aligned,
    PendingAlignment
}

internal readonly record struct MapFloorIdentityResolution<T>(
    T? Identity,
    MapFloorIdentityState State)
    where T : class;

internal static class MapFloorIdentityRules
{
    public static MapFloorIdentityResolution<T> Resolve<T>(
        T? aligned,
        T? pending)
        where T : class => aligned is not null
            ? new MapFloorIdentityResolution<T>(aligned, MapFloorIdentityState.Aligned)
            : pending is not null
                ? new MapFloorIdentityResolution<T>(
                    pending,
                    MapFloorIdentityState.PendingAlignment)
                : new MapFloorIdentityResolution<T>(null, MapFloorIdentityState.None);
}

internal readonly record struct MapFloorSwitchDecision(
    bool Succeeded,
    string? FromFloorKey,
    string? ToFloorKey,
    MapFloorSwitchFailure Failure)
{
    public static MapFloorSwitchDecision Next(
        MapRecord map,
        string? currentFloorKey)
    {
        var floors = MapFloorRules.GetOrderedFloors(map);
        if (floors.Count == 0)
        {
            return new MapFloorSwitchDecision(
                false,
                currentFloorKey,
                null,
                MapFloorSwitchFailure.NoFloors);
        }

        if (floors.Count == 1)
        {
            return new MapFloorSwitchDecision(
                false,
                currentFloorKey ?? floors[0].Key,
                null,
                MapFloorSwitchFailure.NoOtherFloor);
        }

        var fromFloorKey = string.IsNullOrWhiteSpace(currentFloorKey)
            ? floors[0].Key
            : currentFloorKey;
        return new MapFloorSwitchDecision(
            true,
            fromFloorKey,
            MapFloorRules.GetNextFloorKey(map, fromFloorKey),
            MapFloorSwitchFailure.None);
    }

    public static MapFloorSwitchDecision AtPosition(
        MapRecord map,
        string? currentFloorKey,
        int position)
    {
        var floorKey = MapFloorRules.GetFloorKeyAtPosition(map, position);
        return floorKey is null
            ? new MapFloorSwitchDecision(
                false,
                currentFloorKey,
                null,
                MapFloorSwitchFailure.InvalidPosition)
            : new MapFloorSwitchDecision(
                true,
                currentFloorKey,
                floorKey,
                MapFloorSwitchFailure.None);
    }
}
