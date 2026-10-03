namespace IDVBuff.PluginHostMessages;

/// <summary>Markers for input synthesized by the host or a first-party plugin.</summary>
public static class InputInjectionMarkers
{
    public const long HostGeneratedInput = 0x4944564255464652L;

    // Windows mouse input can round-trip ExtraInfo through a 32-bit field,
    // even when both SendInput and MSLLHOOKSTRUCT expose pointer-sized values.
    public const long HostGeneratedMouseInput = HostGeneratedInput & uint.MaxValue;

    public static bool IsHostGeneratedMouseInput(IntPtr extraInfo) =>
        extraInfo.ToInt64() is HostGeneratedInput or HostGeneratedMouseInput;
}
