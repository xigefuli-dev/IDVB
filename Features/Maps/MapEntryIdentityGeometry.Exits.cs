namespace IDVBuff.Features.Maps;

public static partial class MapEntryIdentityGeometry
{
    /// <summary>
    /// Checks all reliable exits in this observation, without searching or granting identity.
    /// Incomplete inventories and missing main-entry polygons cannot prove an outside-inventory conflict.
    /// </summary>
    public static MapEntryIdentityExitEvidence CheckObservedExits(
        IReadOnlyList<MapEntryIdentityPassport> passports, string mapId, string floorKey,
        MapEntryIdentityPose pose, IReadOnlyList<MapEntryIdentityObservation> observations,
        bool inventoryComplete, Func<bool> canCompute)
    {
        var checks = new List<MapEntryIdentityExitCheck>();
        var items = passports.Where(p => p.MapId == mapId && p.FloorKey == floorKey).ToArray();
        if (pose.Scale <= 0 || !double.IsFinite(pose.Scale) || !double.IsFinite(pose.Tx) || !double.IsFinite(pose.Ty) ||
            items.Any(item => !ValidPassport(item))) return new(false, inventoryComplete, checks);
        try
        {
            for (var index = 0; index < observations.Count; index++)
            {
                CheckBudget(canCompute);
                var observed = observations[index];
                if (!Reliable(observed) || !observed.DirectionReliable ||
                    observed.Direction == MapEntryIdentityDirection.Unknown || !Finite(observed.IconCenter)) continue;
                var nearby = items.Where(p => Distance(pose.Apply(p.CenterSource), observed.IconCenter) <=
                    Math.Max(6, p.Unit * pose.Scale * .2)).ToArray();
                var applicable = nearby.Where(p => p.Family == "side" && p.DirectionKnown).ToArray();
                var contradicted = applicable.Length > 0 && applicable.Length == nearby.Length &&
                    applicable.All(p => !p.OutwardDirections.Contains(observed.Direction));
                var mainPossible = false;
                var mainOutlineUnavailable = false;
                foreach (var item in items.Where(p => p.Family == "main"))
                {
                    CheckBudget(canCompute);
                    if (item.MainOutlineSource.Count < 3)
                    {
                        mainOutlineUnavailable = true;
                        continue;
                    }
                    var polygon = item.MainOutlineSource.Select(pose.Apply).ToArray();
                    if (InOrNearPolygon(observed.IconCenter, polygon, 6)) mainPossible = true;
                }
                var outside = inventoryComplete && nearby.Length == 0 && !mainPossible && !mainOutlineUnavailable;
                checks.Add(new(index, applicable.Length > 0 || outside, contradicted || outside, mainPossible,
                    nearby.Select(p => p.SourceEntryId).ToArray(), outside ? "exit-outside-complete-source-inventory" :
                    mainOutlineUnavailable && nearby.Length == 0 ? "source-main-outline-unavailable" : "nearby-entry-direction"));
            }
        }
        catch (BudgetExpiredException) { return new(false, inventoryComplete, checks.ToArray()); }
        return new(true, inventoryComplete, checks.ToArray());
    }

    private static bool InOrNearPolygon(MapEntryIdentityPoint point, MapEntryIdentityPoint[] polygon, double tolerance)
    {
        var inside = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared > 0 ? Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
            if (Distance(point, new(a.X + t * dx, a.Y + t * dy)) <= tolerance) return true;
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
