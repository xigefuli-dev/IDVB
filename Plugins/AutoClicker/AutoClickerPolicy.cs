namespace IDVBuff.Plugins.AutoClicker;

/// <summary>Pure timing and trigger rules for the auto-clicker.</summary>
public static class AutoClickerPolicy
{
    /// <summary>Physical right-button down message.</summary>
    public const uint TriggerMouseDownMessage = 0x0204;

    /// <summary>Physical right-button up message.</summary>
    public const uint TriggerMouseUpMessage = 0x0205;

    /// <summary>Virtual key emitted by the clicker: F (VK_F = 0x46).</summary>
    public const ushort OutputVirtualKey = 0x46;

    /// <summary>Hold duration required before taking over the right button.</summary>
    public const int HoldBeforeClickMilliseconds = 100;

    /// <summary>
    /// Target period of one complete F-down/F-up event. Missed periods are
    /// skipped instead of replayed as a burst.
    /// </summary>
    public const int ClickIntervalMilliseconds = 15;

    public static bool IsTriggerMouseMessage(uint message) =>
        message is TriggerMouseDownMessage or TriggerMouseUpMessage;

    public static bool ShouldStartClicking(bool physicalDown, double heldMilliseconds) =>
        physicalDown && heldMilliseconds >= HoldBeforeClickMilliseconds;
}
