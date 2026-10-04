using IDVBuff.PluginContracts;
using System.Text.Json;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>按游戏客户区比例选择内置 JSON 背包坐标及 SDK 道具栏坐标。</summary>
public static class SceneQuickActionsDropPlan
{
    private static readonly Lazy<Dictionary<string, PluginInventoryCoordinate[]>> BagProfiles =
        new(LoadBagProfiles);

    public static bool TryGetSlots(int width, int height, bool isBag, int hotbarSlot,
        out PluginInventoryCoordinate[] slots)
    {
        slots = [];
        if (width <= 0 || height <= 0)
            return false;

        var coordinates = (long)width * 9 == (long)height * 16
            ? PluginInventoryScale.AspectRatio16By9
            : (long)width * 10 == (long)height * 16
                ? PluginInventoryScale.AspectRatio16By10
                : null;
        if (coordinates is null)
            return false;

        if (isBag)
        {
            var profile = (long)width * 9 == (long)height * 16 ? "16:9" : "16:10";
            slots = (PluginInventoryCoordinate[])BagProfiles.Value[profile].Clone();
        }
        else
        {
            if (hotbarSlot is < 0 or >= 4)
                return false;
            slots = [coordinates.Where(item => item.Shape == 3).ElementAt(hotbarSlot)];
        }
        return true;
    }

    private static Dictionary<string, PluginInventoryCoordinate[]> LoadBagProfiles()
    {
        using var stream = typeof(SceneQuickActionsDropPlan).Assembly.GetManifestResourceStream(
            "IDVB.SceneQuickActions.InventoryCoordinates.json")
            ?? throw new InvalidDataException("Embedded inventory coordinates are missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("units").GetString() != "normalized"
            || root.GetProperty("origin").GetString() != "top-left"
            || root.GetProperty("yAxis").GetString() != "down")
            throw new InvalidDataException("Unsupported inventory coordinate convention.");

        var profiles = new Dictionary<string, PluginInventoryCoordinate[]>(StringComparer.Ordinal);
        foreach (var ratio in new[] { "16:9", "16:10" })
        {
            var profile = root.GetProperty("profiles").GetProperty(ratio);
            var slots = new List<PluginInventoryCoordinate>(6);
            foreach (var (group, shape) in new[] { ("firstThree", 1), ("lastThree", 2) })
            {
                var entries = profile.GetProperty(group);
                if (entries.GetArrayLength() != 3)
                    throw new InvalidDataException($"Invalid inventory group: {ratio}/{group}.");
                foreach (var entry in entries.EnumerateArray())
                {
                    var x = entry.GetProperty("x").GetDouble();
                    var y = entry.GetProperty("y").GetDouble();
                    if (entry.GetProperty("slot").GetInt32() != slots.Count + 1
                        || !double.IsFinite(x) || !double.IsFinite(y)
                        || x is < 0 or > 1 || y is < 0 or > 1)
                        throw new InvalidDataException($"Invalid inventory slot in {ratio}.");
                    slots.Add(new PluginInventoryCoordinate(shape, x, y));
                }
            }
            profiles.Add(ratio, slots.ToArray());
        }
        return profiles;
    }
}
