using IDVBuff.Views;

namespace IDVBuff.Tests;

public sealed class DeepScanVisualRulesTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(65)]
    [InlineData(100)]
    public void ReferenceTextureHasSparseLeftBrokenMiddleAndBrightRight(int columns)
    {
        var cells = Enumerable.Range(0, columns).SelectMany(x =>
            Enumerable.Range(0, 8).Select(y => ScanModeVisualRules.LatticeCell(x, y, columns))).ToArray();
        foreach (var cell in cells)
        {
            Assert.InRange(cell.Floor, 0, 1);
            Assert.InRange(cell.Floor + cell.Range, 0, 1);
        }
        var left = cells.Where(c => c.Progress < .1f).ToArray();
        var middle = cells.Where(c => c.Progress is > .2f and < .6f).ToArray();
        var right = cells.Where(c => c.Progress > .9f).ToArray();
        Assert.True(left.Max(c => c.Floor + c.Range) < right.Min(c => c.Floor));
        Assert.True(left.Count(c => c.Visible) < left.Length * .15);
        Assert.Contains(middle, c => !c.Visible);
        Assert.Contains(middle, c => c.Visible && c.Range > .1f);
        Assert.All(right, c => Assert.True(c.Visible));
        Assert.True(cells.Select(c => c.Range).Distinct().Count() > 10);
        Assert.True(cells.Select(c => 700 + c.Seed % 1100).Distinct().Count() > 100);
        Assert.Equal(ScanModeVisualRules.LatticeCell(14, 5, columns),
            ScanModeVisualRules.LatticeCell(14, 5, columns));
    }

    [Fact]
    public void NonlinearResponseStartsImmediatelyAndSettlesWithoutOvershoot()
    {
        Assert.Equal(0, ScanModeVisualRules.ResponseProgress(0, 150));
        Assert.InRange(ScanModeVisualRules.ResponseProgress(30, 150), .67, .68);
        Assert.InRange(ScanModeVisualRules.ResponseProgress(60, 150), .92, .93);
        Assert.Equal(1, ScanModeVisualRules.ResponseProgress(150, 150));
        Assert.Equal(1, ScanModeVisualRules.ResponseProgress(500, 150));
        var values = Enumerable.Range(0, 151).Select(t => ScanModeVisualRules.ResponseProgress(t, 150)).ToArray();
        for (var i = 1; i < values.Length; i++) Assert.True(values[i] >= values[i - 1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.4)]
    [InlineData(1)]
    public void PointerPoseAndLatticeClipUseTheDisplayedPosition(double expansion)
    {
        const double width = 348;
        var segment = width / (3 + expansion);
        foreach (var x in new[] { 130d, 180d, 235d })
        {
            var index = ScanModeVisualRules.PointerIndex(x, width, expansion, 4);
            var left = index * segment + 4;
            var displayedCenter = left + (segment - 8) / 2;
            Assert.Equal(x, displayedCenter, 9);
            Assert.Equal(left, width - ScanModeVisualRules.FillRightInset(width, left), 9);
        }
        Assert.Equal(width, ScanModeVisualRules.FillRightInset(width, -10));
        Assert.Equal(0, ScanModeVisualRules.FillRightInset(width, width + 10));
        foreach (var x in new[] { -500d, 0d, width, width + 500 })
        {
            var index = ScanModeVisualRules.PointerIndex(x, width, expansion, 4);
            var left = index * segment + 4;
            Assert.InRange(left, 4, width - (segment - 8) - 4 + 1e-9);
        }
    }

    [Fact]
    public void FourPersistentSlotsCanBeSelectedDirectlyAtEverySupportedWidth()
    {
        foreach (var width in new[] { 240d, 348d, 520d })
        {
            for (var mode = 0; mode < 4; mode++)
            {
                var center = width * (mode + .5) / 4;
                Assert.Equal(mode, ScanModeVisualRules.HitTest(center, width, 1, 4));
                Assert.Equal(mode, ScanModeVisualRules.PointerIndex(center, width, 1, 4));
                Assert.Equal(mode, ScanModeVisualRules.HitTest(width * mode / 4, width, 1, 4));
            }
            Assert.Equal(0, ScanModeVisualRules.HitTest(-20, width, 1, 4));
            Assert.Equal(3, ScanModeVisualRules.HitTest(width + 20, width, 1, 4));
        }
    }
}
