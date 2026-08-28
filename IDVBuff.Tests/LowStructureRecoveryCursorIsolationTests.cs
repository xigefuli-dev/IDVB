using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class LowStructureRecoveryCursorIsolationTests
{
    [Fact]
    public void RecoveryCursorCannotLeakAcrossMapOrResolutionSwitches()
    {
        var cursor = new LowStructureRecoveryCursor();
        var grid = new[] { 0.40d, 0.50d, 0.60d, 0.70d, 0.80d };
        const string mapAAt1K = "map-a|b1f|match|1920x1080|viewport-a|config";
        const string mapBAt1K = "map-b|b1f|match|1920x1080|viewport-a|config";
        const string mapBAt2K = "map-b|b1f|match|2560x1600|viewport-b|config";

        var mapAFirst = cursor.TakeBatch(mapAAt1K, grid, 3);
        var mapASecond = cursor.TakeBatch(mapAAt1K, grid, 3);
        var mapBFirst = cursor.TakeBatch(mapBAt1K, grid, 3);
        var mapBResolutionSwitch = cursor.TakeBatch(mapBAt2K, grid, 3);

        Assert.Empty(mapAFirst.Intersect(mapASecond));
        Assert.Equal(mapAFirst, mapBFirst);
        Assert.Equal(mapAFirst, mapBResolutionSwitch);
    }
}
