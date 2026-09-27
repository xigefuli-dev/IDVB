using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapObservationRenderingTests
{
    [Theory]
    [InlineData(96u)]
    [InlineData(144u)]
    [InlineData(192u)]
    public void PendingRegionIsDashedYellowAndClearsIndependentlyOfStatus(uint dpi)
    {
        var scene = new MapOverlayRenderScene(320, 200, dpi, null, null, false,
            ObservationRegion: new MapScreenRect(40, 30, 200, 120));
        using var pending = MapOverlayBitmapRenderer.Render(scene);
        using var confirmed = MapOverlayBitmapRenderer.Render(scene with { ObservationRegion = null });
        var yellowColumns = 0;
        var gapColumns = 0;
        for (var x = 50; x < 230; x++)
        {
            var hasYellow = false;
            for (var y = 30; y < 37; y++)
            {
                var p = pending.GetPixel(x, y);
                hasYellow |= p.A > 100 && p.R > 230 && p.G > 180 && p.B < 120;
            }
            if (hasYellow) yellowColumns++; else gapColumns++;
        }
        Assert.InRange(yellowColumns, 40, 155);
        Assert.InRange(gapColumns, 20, 140);
        Assert.Equal(0, pending.GetPixel(140, 90).A);
        Assert.Equal(0, pending.GetPixel(20, 20).A);
        for (var x = 0; x < confirmed.Width; x++)
        for (var y = 0; y < confirmed.Height; y++)
            Assert.Equal(0, confirmed.GetPixel(x, y).A);

        // Optional local visual evidence, produced by the actual runtime renderer.
        if (Environment.GetEnvironmentVariable("IDVB_OBSERVATION_RENDER_OUTPUT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using var comparison = new Bitmap(640, 200);
            using var graphics = Graphics.FromImage(comparison);
            graphics.Clear(Color.FromArgb(28, 32, 38));
            graphics.DrawImageUnscaled(pending, 0, 0);
            graphics.DrawImageUnscaled(confirmed, 320, 0);
            comparison.Save(Path.Combine(directory, $"observation-border-{dpi}.png"), ImageFormat.Png);
        }
    }
}
