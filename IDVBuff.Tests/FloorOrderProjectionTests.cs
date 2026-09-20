using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class FloorOrderProjectionTests
{
    [Theory]
    [InlineData(0, "c", "a", "b", "d", "e")]
    [InlineData(2, "a", "b", "c", "d", "e")]
    [InlineData(4, "a", "b", "d", "e", "c")]
    public void MoveToInsertionShiftsTheRowMajorSuccessors(
        int insertionIndex,
        params string[] expected)
    {
        var entries = new[] { "a", "b", "c", "d", "e" };

        var projected = FloorOrderProjection.MoveToInsertion(entries, "c", insertionIndex);

        Assert.Equal(expected, projected);
    }

    [Fact]
    public void MoveToInsertionClampsTheAppendSlot()
    {
        var projected = FloorOrderProjection.MoveToInsertion(
            new[] { "1f", "2f", "3f" },
            "1f",
            99);

        Assert.Equal(new[] { "2f", "3f", "1f" }, projected);
    }
}
