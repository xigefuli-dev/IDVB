using IDVBuff.PluginContracts;
using IDVBuff.Plugins.SceneQuickActions;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "12")]
public sealed class SceneQuickActionsBindingExecutorTests
{
    [Theory]
    [InlineData(0x12u, "K11:down,K12:down,K11:up")]
    [InlineData(0x48u, "K11:down,K12:down,K10:down,K47:down,K48:down,K47:up,K10:up,K12:up,K11:up")]
    [InlineData(0x49u, "K11:down,K12:down,K10:down,K47:down,K48:down,K49:down,K48:up,K47:up,K10:up,K12:up,K11:up")]
    public void FailedPressReleasesExactlyTheKeysThatWereSuccessfullyPressed(uint failedKey, string expected)
    {
        var input = new FaultInput { FailPressKey = failedKey };

        var exception = Assert.Throws<InvalidOperationException>(() => input.Execute(KeyboardBinding()));

        Assert.Equal($"Press failed: {failedKey:X}", exception.Message);
        Assert.Equal(expected.Split(','), input.Events);
        Assert.Empty(input.PressedKeys);
        Assert.Equal(0, input.HoldCount);
    }

    [Fact]
    public void FailedMousePressStillReleasesModifiersAndCompanions()
    {
        var input = new FaultInput { FailMousePress = true };

        Assert.Throws<InvalidOperationException>(() => input.Execute(MouseBinding()));

        Assert.Equal(["K11:down", "K47:down", "Left:down", "K47:up", "K11:up"], input.Events);
        Assert.Empty(input.PressedKeys);
        Assert.False(input.MousePressed);
        Assert.Equal(0, input.HoldCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoldFailureReleasesThePrimaryInputBeforeItsCompanionsAndModifiers(bool mouse)
    {
        var input = new FaultInput { FailHold = true };

        Assert.Throws<OperationCanceledException>(() => input.Execute(mouse ? MouseBinding() : KeyboardBinding()));

        Assert.Empty(input.PressedKeys);
        Assert.False(input.MousePressed);
        if (mouse)
            Assert.Equal(["K11:down", "K47:down", "Left:down", "Left:up", "K47:up", "K11:up"], input.Events);
        else
            Assert.Equal(["K11:down", "K12:down", "K10:down", "K47:down", "K48:down", "K49:down",
                "K49:up", "K48:up", "K47:up", "K10:up", "K12:up", "K11:up"], input.Events);
    }

    [Fact]
    public void FailedReleaseDoesNotSkipReleasingOtherOwnedInputs()
    {
        var input = new FaultInput { FailReleaseKey = 0x48 };

        var exception = Assert.Throws<InvalidOperationException>(() => input.Execute(KeyboardBinding()));

        Assert.Equal("Release failed: 48", exception.Message);
        Assert.Equal([0x48u], input.PressedKeys);
        Assert.Equal(["K49:up", "K48:up", "K47:up", "K10:up", "K12:up", "K11:up"], input.Events.TakeLast(6));
    }

    [Fact]
    public void PressAndReleaseFailuresBothRemainVisibleAfterAttemptingAllCleanup()
    {
        var input = new FaultInput { FailPressKey = 0x49, FailReleaseKey = 0x48 };

        var exception = Assert.Throws<AggregateException>(() => input.Execute(KeyboardBinding()));

        Assert.Equal(["Press failed: 49", "Release failed: 48"], exception.InnerExceptions.Select(value => value.Message));
        Assert.Equal([0x48u], input.PressedKeys);
        Assert.Equal("K11:up", input.Events.Last());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessfulChordReleasesAllOwnedInputsInReverseOrder(bool mouse)
    {
        var input = new FaultInput();

        input.Execute(mouse ? MouseBinding() : KeyboardBinding());

        Assert.Empty(input.PressedKeys);
        Assert.False(input.MousePressed);
        Assert.Equal(1, input.HoldCount);
        Assert.Equal("K11:up", input.Events.Last());
    }

    private static PluginInputBinding KeyboardBinding() => PluginInputBinding.Keyboard(0x49,
        PluginInputModifiers.Control | PluginInputModifiers.Alt | PluginInputModifiers.Shift, [0x47, 0x48]);

    private static PluginInputBinding MouseBinding() => new()
    {
        Kind = PluginInputBindingKind.Mouse,
        MouseButton = PluginMouseButton.Left,
        Modifiers = PluginInputModifiers.Control,
        CompanionVirtualKeys = [0x47]
    };

    private sealed class FaultInput
    {
        public List<string> Events { get; } = new();
        public HashSet<uint> PressedKeys { get; } = new();
        public bool MousePressed { get; private set; }
        public uint? FailPressKey { get; init; }
        public uint? FailReleaseKey { get; init; }
        public bool FailMousePress { get; init; }
        public bool FailHold { get; init; }
        public int HoldCount { get; private set; }

        public void Execute(PluginInputBinding binding) => SceneQuickActionsBindingExecutor.Execute(
            binding, 35, SendKeyboard, SendMouse, Hold);

        private void SendKeyboard(uint key, bool up)
        {
            Events.Add($"K{key:X}:{(up ? "up" : "down")}");
            if (!up && key == FailPressKey) throw new InvalidOperationException($"Press failed: {key:X}");
            if (up && key == FailReleaseKey) throw new InvalidOperationException($"Release failed: {key:X}");
            if (up) PressedKeys.Remove(key);
            else PressedKeys.Add(key);
        }

        private void SendMouse(PluginMouseButton button, bool up)
        {
            Events.Add($"{button}:{(up ? "up" : "down")}");
            if (!up && FailMousePress) throw new InvalidOperationException("Mouse press failed.");
            MousePressed = !up;
        }

        private void Hold(int milliseconds)
        {
            HoldCount++;
            Assert.Equal(35, milliseconds);
            if (FailHold) throw new OperationCanceledException("Controlled cancellation while holding the chord.");
        }
    }
}
