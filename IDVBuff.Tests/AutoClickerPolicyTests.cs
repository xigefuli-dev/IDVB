using IDVBuff.Plugins.AutoClicker;
using Xunit;

namespace IDVBuff.Tests;

public class AutoClickerPolicyTests
{
    [Theory]
    [InlineData(0x0204, true)]
    [InlineData(0x0205, true)]
    [InlineData(0x0201, false)]
    [InlineData(0x0100, false)]
    public void IsTriggerMouseMessage_OnlyRightButton_IsTracked(uint message, bool expected) =>
        Assert.Equal(expected, AutoClickerPolicy.IsTriggerMouseMessage(message));

    [Fact]
    public void OutputVirtualKey_IsF() =>
        Assert.Equal(0x46, AutoClickerPolicy.OutputVirtualKey);

    [Theory]
    [InlineData(true, 99.0, false)]
    [InlineData(true, 100.0, true)]
    [InlineData(true, 101.0, true)]
    [InlineData(false, 1000.0, false)]
    public void ShouldStartClicking_HitsHoldThreshold(
        bool physicalDown, double heldMilliseconds, bool expected) =>
        Assert.Equal(
            expected,
            AutoClickerPolicy.ShouldStartClicking(physicalDown, heldMilliseconds));
}
