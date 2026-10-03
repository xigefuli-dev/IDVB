namespace IDVBuff.PluginContracts;

/// <summary>Win32 mouse messages and SendInput use different X-button data layouts.</summary>
public static class PluginMouseInput
{
    public static (uint Flags, uint Data) Encode(PluginMouseButton button, bool down) => button switch
    {
        PluginMouseButton.Left => (down ? 0x0002u : 0x0004u, 0),
        PluginMouseButton.Right => (down ? 0x0008u : 0x0010u, 0),
        PluginMouseButton.Middle => (down ? 0x0020u : 0x0040u, 0),
        PluginMouseButton.XButton1 => (down ? 0x0080u : 0x0100u, 1),
        PluginMouseButton.XButton2 => (down ? 0x0080u : 0x0100u, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    public static bool TryDecode(uint message, uint mouseData,
        out PluginMouseButton button, out bool down)
    {
        down = message is 0x0201 or 0x0204 or 0x0207 or 0x020B;
        button = message switch
        {
            0x0201 or 0x0202 => PluginMouseButton.Left,
            0x0204 or 0x0205 => PluginMouseButton.Right,
            0x0207 or 0x0208 => PluginMouseButton.Middle,
            0x020B or 0x020C when (mouseData >> 16) == 1 => PluginMouseButton.XButton1,
            0x020B or 0x020C when (mouseData >> 16) == 2 => PluginMouseButton.XButton2,
            _ => (PluginMouseButton)(-1)
        };
        return Enum.IsDefined(button);
    }
}
