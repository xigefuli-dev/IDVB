namespace IDVBuff.Plugins.AutoGatling;

/// <summary>自动加特林可由插件设置实时调整的操作时序。</summary>
public sealed class AutoGatlingOptions
{
    public const int MaximumDelayMilliseconds = 10000;

    public int StandardDelayMilliseconds { get; set; } = 50;

    public int ReloadDelayMilliseconds { get; set; } = 2000;

    public int KeyPressDelayMilliseconds { get; set; } = 10;

    public int DragDelayMilliseconds { get; set; } = 50;

    public int MinimumRandomDelayMilliseconds { get; set; } = 10;

    public int MaximumRandomDelayMilliseconds { get; set; } = 20;

    public int CoerceDelay(int value) =>
        Math.Clamp(value, 0, MaximumDelayMilliseconds);

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var first = CoerceDelay(MinimumRandomDelayMilliseconds);
        var second = CoerceDelay(MaximumRandomDelayMilliseconds);
        return (Math.Min(first, second), Math.Max(first, second));
    }
}
