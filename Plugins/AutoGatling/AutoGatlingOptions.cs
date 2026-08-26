using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.AutoGatling;

/// <summary>自动加特林可由插件设置实时调整的操作时序。</summary>
public sealed class AutoGatlingOptions
{
    public const int MaximumDelayMilliseconds = 10000;
    public const int MinimumRandomDelayMillisecondsAllowed = 30;
    public const int MinimumRandomDelayUpperBoundMillisecondsAllowed = 50;
    public const int DefaultMaximumRandomDelayMilliseconds = 50;
    public const int MaximumActivationCycleCount = 2;

    public int EquipmentSlotCount { get; set; } = 2;

    public int ActivationCycleCount { get; set; } = 1;

    public int StandardDelayMilliseconds { get; set; } = 50;

    public int ReloadDelayMilliseconds { get; set; } = 2000;

    public int KeyPressDelayMilliseconds { get; set; } = 10;

    public int DragDelayMilliseconds { get; set; } = 50;

    private int _minimumRandomDelayMilliseconds = MinimumRandomDelayMillisecondsAllowed;
    private int _maximumRandomDelayMilliseconds = DefaultMaximumRandomDelayMilliseconds;

    public int MinimumRandomDelayMilliseconds
    {
        get => _minimumRandomDelayMilliseconds;
        set => _minimumRandomDelayMilliseconds = CoerceRandomDelay(value);
    }

    public int MaximumRandomDelayMilliseconds
    {
        get => _maximumRandomDelayMilliseconds;
        set => _maximumRandomDelayMilliseconds = CoerceRandomDelayUpperBound(value);
    }

    public int CoerceDelay(int value) =>
        Math.Clamp(value, 0, MaximumDelayMilliseconds);

    public int CoerceRandomDelay(int value) =>
        Math.Clamp(value, PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayMillisecondsAllowed), MaximumDelayMilliseconds);

    public int CoerceRandomDelayUpperBound(int value) =>
        Math.Clamp(value, PluginRandomDelayPolicy.GetMinimum(MinimumRandomDelayUpperBoundMillisecondsAllowed), MaximumDelayMilliseconds);

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var first = CoerceRandomDelay(MinimumRandomDelayMilliseconds);
        var second = CoerceRandomDelayUpperBound(MaximumRandomDelayMilliseconds);
        return (Math.Min(first, second), Math.Max(first, second));
    }
}
