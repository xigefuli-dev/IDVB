using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoGatling;

/// <summary>自动加特林的固定操作参数与坐标选择规则。</summary>
public static class AutoGatlingPlan
{
    public const uint DefaultInventoryVirtualKey = 0x09; // Tab
    public const uint DefaultActivateVirtualKey = 0x54; // T
    public const uint DefaultReloadVirtualKey = 0x59; // Y
    public const uint ReloadVirtualKey = 0x52; // R

    public const int InventorySlotCount = 6;

    public static IReadOnlyList<int> GetInventorySlotSequence(
        int equipmentSlotCount) => equipmentSlotCount switch
    {
        2 => [1, 2],
        4 => [1, 2, 3, 4],
        6 => [1, 2, 3, 4, 5, 6],
        _ => throw new ArgumentOutOfRangeException(
            nameof(equipmentSlotCount),
            equipmentSlotCount,
            "装备方案只支持双枪、四枪或六枪。")
    };

    public static PluginInventoryCoordinate GetInventorySlot(
        IReadOnlyList<PluginInventoryCoordinate> coordinates,
        int slot)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (slot is < 1 or > InventorySlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        var shape = slot <= 3 ? 1 : 2;
        var indexInShape = slot <= 3 ? slot - 1 : slot - 4;
        return coordinates
            .Where(coordinate => coordinate.Shape == shape)
            .ElementAt(indexInShape);
    }

    public static PluginInventoryCoordinate GetHotbarSlot(
        IReadOnlyList<PluginInventoryCoordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        return coordinates.First(coordinate => coordinate.Shape == 3);
    }

    public static bool TryGetCoordinates(
        int width,
        int height,
        out IReadOnlyList<PluginInventoryCoordinate>? coordinates)
    {
        coordinates = null;
        if (width <= 0 || height <= 0)
            return false;

        if ((long)width * 9 == (long)height * 16)
        {
            coordinates = PluginInventoryScale.AspectRatio16By9;
            return true;
        }

        if ((long)width * 10 == (long)height * 16)
        {
            coordinates = PluginInventoryScale.AspectRatio16By10;
            return true;
        }

        return false;
    }
}
