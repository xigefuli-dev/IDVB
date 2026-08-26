using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.NoRecoveryDelay;

public static class NoRecoveryDelayPlan
{
    public const uint DefaultInventoryVirtualKey = 0x09;
    public const uint DefaultActivateVirtualKey = 0x54;

    public static PluginInventoryCoordinate GetInventorySlot(
        IReadOnlyList<PluginInventoryCoordinate> coordinates, int slot)
    {
        if (slot is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(slot));
        var shape = slot <= 3 ? 1 : 2;
        return coordinates.Where(item => item.Shape == shape)
            .ElementAt(slot <= 3 ? slot - 1 : slot - 4);
    }

    public static PluginInventoryCoordinate GetEquipmentSlot(
        IReadOnlyList<PluginInventoryCoordinate> coordinates, int slot)
    {
        if (slot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(slot));
        return coordinates.Where(item => item.Shape == 3).ElementAt(slot - 1);
    }

    public static bool TryGetCoordinates(int width, int height,
        out IReadOnlyList<PluginInventoryCoordinate>? coordinates)
    {
        coordinates = (long)width * 9 == (long)height * 16
            ? PluginInventoryScale.AspectRatio16By9
            : (long)width * 10 == (long)height * 16
                ? PluginInventoryScale.AspectRatio16By10
                : null;
        return coordinates is not null;
    }
}
