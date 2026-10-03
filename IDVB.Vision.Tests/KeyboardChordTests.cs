using IDVBuff.Features.Maps;

namespace IDVB.Vision.Tests;

public sealed class KeyboardChordTests
{
    [Theory]
    [InlineData(0x12, 0x46, 0x43)]
    [InlineData(0x43, 0x46, 0x12)]
    [InlineData(0x46, 0x12, 0x43)]
    public void ThreeKeysAreRequiredInAnyPressOrderAndRepeatsProduceNoExtraEdge(int first, int second, int third)
    {
        var binding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 0x43, Modifiers = MapInputModifiers.Alt, CompanionVirtualKeys = [0x46] };
        var latch = new MapKeyboardChordLatch();
        var pressed = new HashSet<uint>();
        pressed.Add((uint)first); Assert.Null(latch.Observe(binding, pressed.Contains));
        pressed.Add((uint)second); Assert.Null(latch.Observe(binding, pressed.Contains));
        pressed.Add((uint)third); Assert.True(latch.Observe(binding, pressed.Contains));
        Assert.Null(latch.Observe(binding, pressed.Contains));
        pressed.Remove(0x46); Assert.False(latch.Observe(binding, pressed.Contains));
        Assert.Null(latch.Observe(binding, pressed.Contains));
        pressed.Add(0x46); Assert.True(latch.Observe(binding, pressed.Contains));
    }

    [Fact]
    public void RegisteringWhileHeldDoesNotStartAMatchAndAltCAloneNeverFires()
    {
        var binding = new MapInputBinding { Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 0x43, Modifiers = MapInputModifiers.Alt, CompanionVirtualKeys = [0x46] };
        var pressed = new HashSet<uint> { 0x12, 0x43, 0x46 };
        var latch = new MapKeyboardChordLatch();
        latch.Initialize(binding, pressed.Contains);
        Assert.Null(latch.Observe(binding, pressed.Contains));
        pressed.Remove(0x46); Assert.False(latch.Observe(binding, pressed.Contains));
        Assert.Null(latch.Observe(binding, pressed.Contains));
    }
}
