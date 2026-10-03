namespace IDVBuff.Features.Maps;

/// <summary>One edge when the whole chord becomes pressed, one when any required key is released.</summary>
public sealed class MapKeyboardChordLatch
{
    private bool _pressed;
    public void Initialize(MapInputBinding binding, Func<uint, bool> isDown) => _pressed = IsPressed(binding, isDown);
    public bool? Observe(MapInputBinding binding, Func<uint, bool> isDown)
    {
        var pressed = IsPressed(binding, isDown);
        if (pressed == _pressed) return null;
        _pressed = pressed;
        return pressed;
    }
    public static bool IsPressed(MapInputBinding binding, Func<uint, bool> down) =>
        down(binding.VirtualKey) && binding.NormalizedCompanionVirtualKeys().All(down)
        && (!binding.Modifiers.HasFlag(MapInputModifiers.Control) || down(0x11) || down(0xA2) || down(0xA3))
        && (!binding.Modifiers.HasFlag(MapInputModifiers.Alt) || down(0x12) || down(0xA4) || down(0xA5))
        && (!binding.Modifiers.HasFlag(MapInputModifiers.Shift) || down(0x10) || down(0xA0) || down(0xA1))
        && (!binding.Modifiers.HasFlag(MapInputModifiers.Windows) || down(0x5B) || down(0x5C));
}
