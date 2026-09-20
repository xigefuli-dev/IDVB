namespace IDVBuff.Features.Maps;

public sealed record MapScanFloorOption(
    string FloorIdentity,
    string DisplayName,
    bool IsEligible,
    string FailureReason);

/// <summary>
/// Resolves the Class-wide scanning floor without treating literal 1F/2F keys
/// as product semantics. A floor is either the map's ordered primary floor or
/// an other floor carrying the optional secondary gate feature.
/// </summary>
public static class MapScanFloorRules
{
    public const string SecondaryGateAnchorKey = "second-floor-primary";

    public static string? NormalizeFloorIdentity(string? floorKey)
    {
        var normalized = floorKey?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? ResolveFloorKey(MapRecord map, string? floorIdentity)
    {
        var normalized = NormalizeFloorIdentity(floorIdentity);
        if (normalized is null)
            return null;
        return MapFloorRules.GetOrderedFloors(map)
            .FirstOrDefault(floor => string.Equals(
                NormalizeFloorIdentity(floor.Key),
                normalized,
                StringComparison.Ordinal))
            ?.Key;
    }

    public static string ResolveScanFloorKey(MapRecord map) =>
        ResolveFloorKey(map, map.ClassProperties?.ScanFloorKey)
        ?? MapFloorRules.GetPrimaryFloorKey(map);

    public static bool IsPrimaryFloor(MapRecord map, string floorKey) =>
        string.Equals(
            NormalizeFloorIdentity(MapFloorRules.GetPrimaryFloorKey(map)),
            NormalizeFloorIdentity(floorKey),
            StringComparison.Ordinal);

    public static RecognitionAnchor? GetScanFeatureAnchor(
        MapRecord map,
        string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        return IsPrimaryFloor(map, floorKey)
            ? profile?.FindAnchor("side-entrance")
            : profile?.FindAnchor(SecondaryGateAnchorKey);
    }

    public static (RecognitionAnchor Main, RecognitionAnchor Side)?
        GetGeometryAnchors(MapRecord map, string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (profile is null)
            return null;

        if (IsPrimaryFloor(map, floorKey))
        {
            var main = profile.FindAnchor("main-entrance");
            var side = profile.FindAnchor("side-entrance");
            return main is null || side is null ? null : (main, side);
        }

        var secondary = profile.FindAnchor(SecondaryGateAnchorKey);
        // Single-gate floors still need a geometry fingerprint to carry the
        // exact floor identity, image paths and reference dimensions through
        // strict structure verification. Both legacy pair slots deliberately
        // point at the same secondary feature; pair geometry is never used by
        // the single-feature route.
        return secondary is null ? null : (secondary, secondary);
    }

    public static bool HasRequiredScanMarkers(MapRecord map, string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (profile is null)
            return false;
        if (!IsPrimaryFloor(map, floorKey))
            return profile.FindAnchor(SecondaryGateAnchorKey)?.IsMarked is true;
        return profile.FindAnchor("main-entrance")?.IsMarked is true
            && profile.FindAnchor("side-entrance")?.IsMarked is true;
    }

    public static IReadOnlyList<MapScanFloorOption> BuildOptions(
        IEnumerable<MapRecord> classMaps)
    {
        var maps = classMaps.ToArray();
        var identities = maps
            .SelectMany(MapFloorRules.GetOrderedFloors)
            .Select(floor => new
            {
                Identity = NormalizeFloorIdentity(floor.Key)!,
                floor.DisplayName,
                floor.SortOrder
            })
            .Where(item => item.Identity is not null)
            .GroupBy(item => item.Identity, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();

        var options = new List<MapScanFloorOption>(identities.Length);
        foreach (var identity in identities)
        {
            var missing = maps
                .Where(map => ResolveFloorKey(map, identity.Identity) is null)
                .Select(map => map.DisplayName)
                .ToArray();
            if (missing.Length > 0)
            {
                options.Add(new MapScanFloorOption(
                    identity.Identity,
                    identity.DisplayName,
                    false,
                    $"此地图类有 {missing.Length} 张地图缺少该楼层。"));
                continue;
            }

            var unmarked = maps
                .Where(map => ResolveFloorKey(map, identity.Identity) is { } floorKey
                    && !HasRequiredScanMarkers(map, floorKey))
                .Select(map => map.DisplayName)
                .ToArray();
            options.Add(new MapScanFloorOption(
                identity.Identity,
                identity.DisplayName,
                unmarked.Length == 0,
                unmarked.Length == 0
                    ? string.Empty
                    : $"有 {unmarked.Length} 张地图未标记所需门特征。"));
        }
        return options;
    }
}
