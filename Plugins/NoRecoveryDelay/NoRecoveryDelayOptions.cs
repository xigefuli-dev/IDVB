using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.NoRecoveryDelay;

public enum NoRecoveryDelayLoopMode
{
    Hold,
    Rounds
}

public sealed class NoRecoveryDelayOptions
{
    public const int MaximumDelayMilliseconds = 10000;
    public int EquipmentSlot { get; set; } = 1;
    public int InventorySlot1 { get; set; } = 1;
    public int InventorySlot2 { get; set; } = 2;
    public NoRecoveryDelayLoopMode LoopMode { get; set; } = NoRecoveryDelayLoopMode.Hold;
    public int LoopCount { get; set; } = 1;
    public int StandardDelayMilliseconds { get; set; } = 50;
    public int KeyPressDelayMilliseconds { get; set; } = 10;
    public int DragDelayMilliseconds { get; set; } = 50;
    public int MinimumRandomDelayMilliseconds { get; set; } = 30;
    public int MaximumRandomDelayMilliseconds { get; set; } = 50;

    public int CoerceDelay(int value) => Math.Clamp(value, 0, MaximumDelayMilliseconds);
    public (int Minimum, int Maximum) GetRandomRange()
    {
        var lower = Math.Clamp(MinimumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(30), MaximumDelayMilliseconds);
        var upper = Math.Clamp(MaximumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(50), MaximumDelayMilliseconds);
        return (Math.Min(lower, upper), Math.Max(lower, upper));
    }
}
