namespace IDVBuff.Features.Maps;

/// <summary>Single source of truth for IDVM vector-route capability.</summary>
public static class MapRouteRules
{
    public static bool SupportsVectorRoutes(MapRecord map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.ClassProperties.ContainsVectorRoutes
            ?? MapContainsVectorRoutes(map);
    }

    public static bool ClassContainsVectorRoutes(IEnumerable<MapRecord> maps) =>
        maps.Any(MapContainsVectorRoutes);

    public static bool MapContainsVectorRoutes(MapRecord map) =>
        MapFloorRules.GetOrderedFloors(map)
            .Select(floor => MapFloorRules.GetFloorProfile(map, floor.Key))
            .Where(profile => profile is not null)
            .Any(profile => profile!.Annotations.Any(annotation => annotation.IsValid));
}
