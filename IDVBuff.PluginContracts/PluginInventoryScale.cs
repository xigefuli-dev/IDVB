namespace IDVBuff.PluginContracts;

/// <summary>
/// 背包或物品栏单个格子的归一化坐标，以及它所属的 Shape。
/// 坐标顺序由各比例的数据列表保持为从左到右。
/// </summary>
public readonly record struct PluginInventoryCoordinate(
    int Shape,
    double X,
    double Y);

/// <summary>PluginSDK 内置的背包和物品栏坐标（Scale）数据。</summary>
public static class PluginInventoryScale
{
    /// <summary>16:9 比例的坐标，Shape 1、2、3 均按从左到右排列。</summary>
    public static IReadOnlyList<PluginInventoryCoordinate> AspectRatio16By9 { get; } =
        Array.AsReadOnly<PluginInventoryCoordinate>(
        [
            new(1, 0.22, 0.59),
            new(1, 0.30, 0.59),
            new(1, 0.37, 0.59),
            new(2, 0.22, 0.72),
            new(2, 0.29, 0.72),
            new(2, 0.36, 0.72),
            new(3, 0.39, 0.92),
            new(3, 0.47, 0.92),
            new(3, 0.56, 0.92),
            new(3, 0.64, 0.92)
        ]);

    /// <summary>16:10 比例的坐标，Shape 1、2、3 均按从左到右排列。</summary>
    public static IReadOnlyList<PluginInventoryCoordinate> AspectRatio16By10 { get; } =
        Array.AsReadOnly<PluginInventoryCoordinate>(
        [
            new(1, 0.22, 0.64),
            new(1, 0.29, 0.64),
            new(1, 0.37, 0.64),
            new(2, 0.22, 0.74),
            new(2, 0.30, 0.74),
            new(2, 0.37, 0.74),
            new(3, 0.39, 0.93),
            new(3, 0.47, 0.93),
            new(3, 0.55, 0.93),
            new(3, 0.64, 0.93)
        ]);
}
